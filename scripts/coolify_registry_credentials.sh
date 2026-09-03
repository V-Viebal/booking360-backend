#!/usr/bin/env bash
# Stage or clear the short-lived GitHub job credential used by one locked
# Coolify Application to pull a private immutable GHCR image.
#
# This script intentionally reads only value-safe metadata in error messages.
# It deletes every matching environment row, including preview-scoped mirrors.

set -Eeuo pipefail

mode="${1:-}"
[[ "${mode}" == "stage" || "${mode}" == "clear" ]] || {
  echo "Usage: $0 <stage|clear>" >&2
  exit 64
}

for required in \
  COOLIFY_URL COOLIFY_API_TOKEN COOLIFY_APPLICATION_UUID \
  COOLIFY_APPLICATION_NAME COOLIFY_PROJECT_UUID COOLIFY_ENVIRONMENT_UUID \
  COOLIFY_DESTINATION_UUID COOLIFY_SERVER_UUID COOLIFY_ENVIRONMENT_NAME \
  COOLIFY_GIT_BRANCH EXPECTED_GITHUB_REPOSITORY; do
  [[ -n "${!required:-}" ]] || {
    printf 'Required deployment variable is missing: %s\n' "${required}" >&2
    exit 1
  }
done

if [[ "${mode}" == "stage" ]]; then
  : "${GHCR_USERNAME:?GHCR_USERNAME is required}"
  : "${GHCR_TOKEN:?GHCR_TOKEN is required}"
fi

api_base="${COOLIFY_URL%/}/api/v1"
application_url="${api_base}/applications/${COOLIFY_APPLICATION_UUID}"
envs_url="${application_url}/envs"
api_args=(
  --fail-with-body
  --silent
  --show-error
  --retry 5
  --retry-all-errors
  --retry-delay 3
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
    --arg domain "${COOLIFY_PUBLIC_DOMAIN:-}" \
    --arg branch "${COOLIFY_GIT_BRANCH}" \
    --arg repo "${EXPECTED_GITHUB_REPOSITORY}" \
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
      and (($domain == "") or ((.docker_compose_domains // .fqdn // "")
        | decode_json_string | tostring | contains($domain)))
    ' <<<"${application_json}" >/dev/null
}

application_json="$(get_application)"
assert_target "${application_json}" || {
  echo "Coolify Application target lock failed; refusing credential mutation." >&2
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

envs_json="$(
  curl "${api_args[@]}" "${auth_args[@]}" \
    --header 'Accept: application/json' "${envs_url}"
)"

delete_key() {
  local key="$1"
  local -a uuids=()
  mapfile -t uuids < <(
    normalize_rows <<<"${envs_json}" |
      jq -r --arg key "${key}" \
        '.[] | select(.key == $key) | (.uuid // empty)'
  )
  if normalize_rows <<<"${envs_json}" |
    jq -e --arg key "${key}" 'any(.[]; .key == $key)' >/dev/null &&
    [[ "${#uuids[@]}" -eq 0 ]]; then
    printf 'Coolify did not expose UUIDs needed to delete %s.\n' "${key}" >&2
    exit 1
  fi
  for uuid in "${uuids[@]}"; do
    curl "${api_args[@]}" "${auth_args[@]}" \
      --request DELETE "${envs_url}/${uuid}" >/dev/null
  done
  envs_json="$(
    curl "${api_args[@]}" "${auth_args[@]}" \
      --header 'Accept: application/json' "${envs_url}"
  )"
}

create_key() {
  local key="$1"
  local value="$2"
  local payload
  delete_key "${key}"
  payload="$(
    jq -cn --arg key "${key}" --arg value "${value}" \
      '{key:$key,value:$value,is_preview:false,is_literal:true,is_runtime:true,is_buildtime:true}'
  )"
  curl "${api_args[@]}" "${auth_args[@]}" \
    --request POST \
    --header 'Content-Type: application/json' \
    --data "${payload}" "${envs_url}" >/dev/null
  envs_json="$(
    curl "${api_args[@]}" "${auth_args[@]}" \
      --header 'Accept: application/json' "${envs_url}"
  )"
}

credential_keys=(
  BOOKING360_BACKEND_GHCR_USERNAME
  BOOKING360_BACKEND_GHCR_TOKEN
)

if [[ "${mode}" == "stage" ]]; then
  create_key "${credential_keys[0]}" "${GHCR_USERNAME}"
  create_key "${credential_keys[1]}" "${GHCR_TOKEN}"
  for key in "${credential_keys[@]}"; do
    normalize_rows <<<"${envs_json}" | jq -e --arg key "${key}" \
      '([.[] | select(.key == $key)] | length) >= 1
       and any(.[]; .key == $key and .is_preview == false)
       and all(.[]; select(.key == $key)
         | ((.value // .real_value // "") | length) > 0)' \
      >/dev/null || {
        echo "Coolify did not persist the temporary GHCR credential contract." >&2
        exit 1
      }
  done
  echo "Temporary GHCR pull credentials staged on the locked Application."
  exit 0
fi

for key in "${credential_keys[@]}"; do
  delete_key "${key}"
done
for key in "${credential_keys[@]}"; do
  normalize_rows <<<"${envs_json}" |
    jq -e --arg key "${key}" 'any(.[]; .key == $key) | not' >/dev/null || {
      echo "Coolify did not clear every temporary GHCR credential row." >&2
      exit 1
    }
done
echo "Temporary GHCR pull credentials are absent from the locked Application."
