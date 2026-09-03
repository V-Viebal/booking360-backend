#!/usr/bin/env bash
# Target-lock, mutate, deploy, poll, verify, and roll back one Booking360
# backend Coolify Application. Secret values are never printed or persisted.

set -Eeuo pipefail

for required in \
  COOLIFY_URL COOLIFY_API_TOKEN COOLIFY_APPLICATION_UUID \
  COOLIFY_APPLICATION_NAME COOLIFY_PROJECT_UUID COOLIFY_ENVIRONMENT_UUID \
  COOLIFY_DESTINATION_UUID COOLIFY_SERVER_UUID COOLIFY_ENVIRONMENT_NAME \
  COOLIFY_GIT_BRANCH COOLIFY_PUBLIC_DOMAIN IMAGE_REF RELEASE_SHA \
  BACKEND_HEALTH_URL GHCR_USERNAME GHCR_TOKEN \
  BOOKING360_BACKEND_COMPOSE_PROJECT BOOKING360_BACKEND_NETWORK_ALIAS; do
  [[ -n "${!required:-}" ]] || {
    printf 'Required deployment variable is missing: %s\n' "${required}" >&2
    exit 1
  }
done

[[ "${IMAGE_REF}" == ghcr.io/v-viebal/booking360-backend@sha256:* ]] || {
  echo "IMAGE_REF must be the exact Booking360 backend GHCR digest." >&2
  exit 1
}

case "${COOLIFY_ENVIRONMENT_NAME}" in
  staging)
    expected_project="booking360-backend-staging"
    expected_alias="booking360-backend-staging"
    router_name="booking360-backend-staging"
    ;;
  production)
    expected_project="booking360-backend-production"
    expected_alias="booking360-backend"
    router_name="booking360-backend-production"
    ;;
  *)
    echo "Unsupported Booking360 backend environment." >&2
    exit 1
    ;;
esac

[[ "${BOOKING360_BACKEND_COMPOSE_PROJECT}" == "${expected_project}" ]] || {
  echo "The Compose project does not match the locked target environment." >&2
  exit 1
}
[[ "${BOOKING360_BACKEND_NETWORK_ALIAS}" == "${expected_alias}" ]] || {
  echo "The internal network alias does not match the locked target environment." >&2
  exit 1
}

is_approved_immutable_image() {
  [[ "$1" == ghcr.io/v-viebal/booking360-backend@sha256:* ]]
}

api_base="${COOLIFY_URL%/}/api/v1"
application_url="${api_base}/applications/${COOLIFY_APPLICATION_UUID}"
envs_url="${application_url}/envs"
api_args=(
  --fail-with-body
  --silent
  --show-error
  --retry 12
  --retry-all-errors
  --retry-delay 5
  --connect-timeout 15
  --max-time 120
)
auth_args=(--header "Authorization: Bearer ${COOLIFY_API_TOKEN}")

get_application() {
  curl "${api_args[@]}" "${auth_args[@]}" \
    --header 'Accept: application/json' "${application_url}"
}

normalize_rows() {
  jq -c 'if type == "array" then . else (.envs // .data // []) end'
}

assert_target() {
  local application_json="$1"
  jq -e \
    --arg uuid "${COOLIFY_APPLICATION_UUID}" \
    --arg name "${COOLIFY_APPLICATION_NAME}" \
    --arg project "${COOLIFY_PROJECT_UUID}" \
    --arg environment "${COOLIFY_ENVIRONMENT_UUID}" \
    --arg environment_name "${COOLIFY_ENVIRONMENT_NAME}" \
    --arg destination "${COOLIFY_DESTINATION_UUID}" \
    --arg server "${COOLIFY_SERVER_UUID}" \
    --arg domain "${COOLIFY_PUBLIC_DOMAIN}" \
    --arg branch "${COOLIFY_GIT_BRANCH}" \
    --arg repo "V-Viebal/booking360-backend" \
    '
      def decode_json_string:
        if type == "string" and (startswith("{") or startswith("["))
        then (try fromjson catch .)
        else .
        end;
      (.uuid == $uuid)
      and (.name == $name)
      and (.git_repository == $repo)
      and (.git_branch == $branch)
      and ((.build_pack // "") == "dockercompose")
      and ((.project_uuid // .environment.project.uuid // "") == $project)
      and ((.environment_uuid // .environment.uuid // "") == $environment)
      and ((.environment.name // "") == $environment_name)
      and ((.destination_uuid // .destination.uuid // "") == $destination)
      and ((.server_uuid // .destination.server.uuid // .server.uuid // "") == $server)
      and ((.docker_compose_location // "") == "/compose.yaml")
      and ((.docker_compose_domains // .fqdn // "")
        | decode_json_string | tostring | contains($domain))
    ' <<<"${application_json}" >/dev/null
}

get_envs() {
  curl "${api_args[@]}" "${auth_args[@]}" \
    --header 'Accept: application/json' "${envs_url}"
}

set_env() {
  local key="$1"
  local value="$2"
  local payload
  local method=POST
  local existing_rows
  payload="$(
    jq -cn --arg key "${key}" --arg value "${value}" \
      '{key:$key,value:$value,is_preview:false,is_literal:true,is_runtime:true,is_buildtime:true}'
  )"
  existing_rows="$(normalize_rows <<<"$(get_envs)")"
  if jq -e --arg key "${key}" \
    'any(.[]; .key == $key and .is_preview == false)' \
    <<<"${existing_rows}" >/dev/null; then
    method=PATCH
  fi
  curl "${api_args[@]}" "${auth_args[@]}" \
    --request "${method}" \
    --header 'Content-Type: application/json' \
    --data "${payload}" "${envs_url}" >/dev/null
  normalize_rows <<<"$(get_envs)" |
    jq -e --arg key "${key}" --arg value "${value}" \
      'any(.[]; .key == $key and .is_preview == false
        and ((.value // .real_value // "") == $value))' >/dev/null
}

set_image() {
  set_env BOOKING360_BACKEND_IMAGE "$1"
}

queue_deployment() {
  local response
  local deployment_uuid
  response="$(
    curl "${api_args[@]}" "${auth_args[@]}" \
      --request POST \
      --header 'Accept: application/json' \
      --header 'Content-Type: application/json' \
      --data "$(jq -cn --arg uuid "${COOLIFY_APPLICATION_UUID}" '{uuid:$uuid}')" \
      "${api_base}/deploy?force=true"
  )"
  deployment_uuid="$(
    jq -r '.deployment_uuid // .deploymentUuid
      // .deployments[0].deployment_uuid // .deployments[0].uuid // empty' \
      <<<"${response}"
  )"
  : "${deployment_uuid:?Coolify did not return a deployment UUID}"
  printf '%s' "${deployment_uuid}"
}

wait_for_deployment() {
  local deployment_uuid="$1"
  local label="$2"
  local deployment
  local state
  for attempt in $(seq 1 120); do
    deployment="$(
      curl "${api_args[@]}" "${auth_args[@]}" \
        --header 'Accept: application/json' \
        "${api_base}/deployments/${deployment_uuid}"
    )"
    state="$(
      jq -r '.status // .deployment_status // .deploymentStatus // .state // "unknown"' \
        <<<"${deployment}" | tr '[:upper:]' '[:lower:]' | tr '_' '-'
    )"
    case "${state}" in
      finished|success|succeeded|completed|successful)
        printf '%s deployment reached terminal success: %s\n' "${label}" "${state}"
        return 0
        ;;
      failed|error|cancelled|canceled|aborted|timeout|timed-out|timed_out)
        printf '%s deployment failed: %s\n' "${label}" "${state}" >&2
        jq -c '{uuid, status, deployment_status, commit, branch}' \
          <<<"${deployment}" >&2 || true
        return 1
        ;;
    esac
    [[ "${attempt}" -lt 120 ]] || {
      printf '%s deployment polling timed out.\n' "${label}" >&2
      return 1
    }
    sleep 5
  done
}

get_deployment() {
  local deployment_uuid="$1"
  curl "${api_args[@]}" "${auth_args[@]}" \
    --header 'Accept: application/json' \
    "${api_base}/deployments/${deployment_uuid}"
}

verify_runtime_identity() {
  local deployment_uuid="$1"
  local deployment_json="$2"
  local logs
  logs="$(
    jq -r '.logs // .output // .deployment_logs // .deploymentLogs // empty' \
      <<<"${deployment_json}" 2>/dev/null || true
  )"
  if ! grep -F "runtime_image_ref=${IMAGE_REF}" <<<"${logs}" >/dev/null ||
    ! grep -F 'repo_digest_match=true' <<<"${logs}" >/dev/null; then
    echo "Coolify deployment logs did not prove exact runtime image identity." >&2
    echo "deployment_uuid=${deployment_uuid}" >&2
    return 1
  fi
  local runtime_image_id
  runtime_image_id="$(
    sed -n 's/.*runtime_image_id=\([^[:space:]]*\).*/\1/p' <<<"${logs}" | tail -n 1
  )"
  [[ -n "${runtime_image_id}" ]] || {
    echo "Coolify deployment logs did not expose a runtime image ID." >&2
    return 1
  }
  printf 'runtime_image_id=%s\n' "${runtime_image_id}" >>"${GITHUB_OUTPUT:-/dev/null}"
}

verify_health_url() {
  local url="$1"
  local stable_required="${HEALTH_STABLE_SAMPLES:-3}"
  local stable=0
  local query_separator='?'
  local response
  [[ "${url}" == *\?* ]] && query_separator='&'
  for attempt in $(seq 1 60); do
    response="$(
      curl --fail --silent --show-error \
        -H 'User-Agent: Booking360-Release-Verify/1.0' \
        -H 'Cache-Control: no-cache, no-store' \
        "${url}${query_separator}release=${RELEASE_SHA:0:12}" 2>/dev/null ||
        true
    )"
    if jq -e '.status == "ok"' <<<"${response}" >/dev/null 2>&1; then
      stable=$((stable + 1))
      if [[ "${stable}" -ge "${stable_required}" ]]; then
        return 0
      fi
    else
      stable=0
    fi
    [[ "${attempt}" -lt 60 ]] || return 1
    sleep 5
  done
}

verify_health() {
  verify_health_url "${BACKEND_HEALTH_URL}"
  if [[ -n "${HEALTH_URL:-}" && "${HEALTH_URL}" != "${BACKEND_HEALTH_URL}" ]]; then
    verify_health_url "${HEALTH_URL}"
  fi
}

application_json="$(get_application)"
assert_target "${application_json}" || {
  echo "Coolify Application identity preflight failed; refusing mutation." >&2
  jq -c '{
    uuid,
    name,
    git_repository,
    git_branch,
    build_pack,
    project_uuid: (.project_uuid // .environment.project.uuid // null),
    environment_uuid: (.environment_uuid // .environment.uuid // null),
    environment_name: (.environment.name // null),
    destination_uuid: (.destination_uuid // .destination.uuid // null),
    server_uuid: (.server_uuid // .destination.server.uuid // .server.uuid // null),
    domains: (.docker_compose_domains // .fqdn // null),
    compose: .docker_compose_location
  }' <<<"${application_json}" >&2
  exit 1
}

envs_json="$(get_envs)"
previous_image="$(
  normalize_rows <<<"${envs_json}" |
    jq -r 'map(select(.key == "BOOKING360_BACKEND_IMAGE" and .is_preview == false))
      | .[0] | (.value // .real_value // empty)'
)"

if is_approved_immutable_image "${previous_image}"; then
  printf 'previous_image=%s\n' "${previous_image}" >>"${GITHUB_OUTPUT:-/dev/null}"
else
  if [[ "${BOOKING360_ALLOW_INITIAL_RELEASE:-false}" == "true" && -z "${previous_image}" ]]; then
    previous_image=""
    printf 'previous_image=\n' >>"${GITHUB_OUTPUT:-/dev/null}"
    echo "No previous image exists; proceeding only because the target explicitly allows its initial release."
  else
    echo "No previous immutable Booking360 backend image exists; rollback is not safe." >&2
    exit 1
  fi
fi

credentials_staged=false
image_mutated=false
deployment_succeeded=false

rollback() {
  [[ -n "${previous_image}" ]] || {
    echo "No previous image was captured; initial release cannot be rolled back automatically." >&2
    return 1
  }
  local rollback_uuid
  echo "Restoring the previous immutable image."
  set_image "${previous_image}"
  rollback_uuid="$(queue_deployment)"
  printf 'rollback_deployment_uuid=%s\n' "${rollback_uuid}" >>"${GITHUB_OUTPUT:-/dev/null}"
  wait_for_deployment "${rollback_uuid}" "Rollback"
  local rollback_json
  rollback_json="$(get_deployment "${rollback_uuid}")"
  verify_runtime_identity "${rollback_uuid}" "${rollback_json}"
  verify_health
}

cleanup() {
  local rc="$?"
  trap - EXIT
  if [[ "${rc}" -ne 0 && "${image_mutated}" == true && "${deployment_succeeded}" == false ]]; then
    rollback || echo "Rollback did not converge; manual intervention is required." >&2
  fi
  if [[ "${credentials_staged}" == true ]]; then
    bash scripts/coolify_registry_credentials.sh clear ||
      echo "Fast-path credential cleanup failed; independent cleanup job must recover it." >&2
  fi
  exit "${rc}"
}
trap cleanup EXIT

# Mark true before stage so a partial stage is still cleaned by the fast path.
credentials_staged=true
bash scripts/coolify_registry_credentials.sh stage

configuration_payload="$(
  jq -cn \
    --arg branch "${COOLIFY_GIT_BRANCH}" \
    --arg revision "${RELEASE_SHA}" \
    --arg compose_location "/compose.yaml" \
    --arg start_command "bash scripts/coolify_ghcr_start.sh" '{
      git_branch:$branch,
      git_commit_sha:$revision,
      docker_compose_location:$compose_location,
      docker_compose_custom_start_command:$start_command,
      pre_deployment_command:null
    }'
)"
curl "${api_args[@]}" "${auth_args[@]}" \
  --request PATCH \
  --header 'Content-Type: application/json' \
  --data "${configuration_payload}" "${application_url}" >/dev/null
application_json="$(get_application)"
assert_target "${application_json}"
jq -e --arg revision "${RELEASE_SHA}" \
  '(.git_commit_sha == $revision)
   and (.docker_compose_custom_start_command == "bash scripts/coolify_ghcr_start.sh")' \
  <<<"${application_json}" >/dev/null || {
  echo "Coolify did not persist the triggering source revision." >&2
  exit 1
}

set_image "${IMAGE_REF}"
image_mutated=true
set_env BOOKING360_BACKEND_COMPOSE_PROJECT "${BOOKING360_BACKEND_COMPOSE_PROJECT}"
set_env BOOKING360_BACKEND_ENVIRONMENT "${COOLIFY_ENVIRONMENT_NAME}"
set_env BOOKING360_BACKEND_NETWORK_ALIAS "${BOOKING360_BACKEND_NETWORK_ALIAS}"
set_env BOOKING360_BACKEND_PUBLIC_DOMAIN "${COOLIFY_PUBLIC_DOMAIN}"
set_env BOOKING360_BACKEND_ROUTER_NAME "${router_name}"
set_env BOOKING360_RELEASE_SHA "${RELEASE_SHA}"
set_env APP_ENV_PREFIX "BOOKING360"
set_env ASPNETCORE_URLS "http://+:8101"
set_env ASPNETCORE_ENVIRONMENT "Production"

runtime_keys=(
  APP_FRONTEND_URL
  BOOKING360_FRONTEND_URL
  BOOKING360_DB_URL
  BOOKING360_DB_LOCAL_URL
  BOOKING360_LOGTO_ISSUER
  BOOKING360_LOGTO_API_RESOURCE_INDICATOR
  BOOKING360_LOGTO_INTERNAL_API
  BOOKING360_MINIO_SERVER_URL
  BOOKING360_MINIO_SECURE
  BOOKING360_MINIO_BUCKET
  BOOKING360_MINIO_LOCAL_BUCKET
  BOOKING360_MINIO_ROOT_USER
  BOOKING360_MINIO_ROOT_PASSWORD
  BOOKING360_MAIL_HOST
  BOOKING360_MAIL_PORT
  BOOKING360_MAIL_USER
  BOOKING360_MAIL_PASSWORD
  BOOKING360_MAIL_SENDER
  BOOKING360_MAIL_SENDER_NAME
  BOOKING360_NOTIFICATION_DEFAULT_CHANNEL
  SMTP_HOST
  SMTP_PORT
  SMTP_USER
  SMTP_PASSWORD
  SENDER_EMAIL
  SENDER_NAME
)
for key in "${runtime_keys[@]}"; do
  if [[ -v "${key}" ]]; then
    set_env "${key}" "${!key}"
  fi
done

deployment_uuid="$(queue_deployment)"
printf 'deployment_uuid=%s\n' "${deployment_uuid}" >>"${GITHUB_OUTPUT:-/dev/null}"
wait_for_deployment "${deployment_uuid}" "Release"
deployment_json="$(get_deployment "${deployment_uuid}")"
verify_runtime_identity "${deployment_uuid}" "${deployment_json}"
verify_health || {
  echo "Public health verification failed after stable retries." >&2
  exit 1
}
deployment_succeeded=true
echo "Exact-digest Booking360 backend release verified."
