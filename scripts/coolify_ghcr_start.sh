#!/usr/bin/env bash
# Coolify custom-start command: pull and verify one private immutable image,
# remove the short-lived registry credential, then start the Compose project.

set -Eeuo pipefail

artifact_dir=""
compose_file=""
candidate_dirs=(
  "${PWD}"
  "${COOLIFY_RESOURCE_UUID:+/artifacts/${COOLIFY_RESOURCE_UUID}}"
  "${COOLIFY_RESOURCE_UUID:+/data/coolify/applications/${COOLIFY_RESOURCE_UUID}}"
)

for candidate in "${candidate_dirs[@]}"; do
  [[ -n "${candidate}" && -f "${candidate}/.env" ]] || continue
  for candidate_compose in compose.yaml docker-compose.yaml; do
    if [[ -f "${candidate}/${candidate_compose}" ]]; then
      artifact_dir="${candidate}"
      compose_file="${candidate}/${candidate_compose}"
      break 2
    fi
  done
done

[[ -n "${artifact_dir}" && -n "${compose_file}" ]] || {
  echo "Coolify deployment artifact does not contain .env and a Compose file." >&2
  exit 1
}

env_file="${artifact_dir}/.env"

read_artifact_value() {
  local key="$1"
  local ambient="${!key:-}"
  if [[ -n "${ambient}" ]]; then
    printf '%s' "${ambient}"
    return
  fi

  awk -F= -v wanted="${key}" '
    $1 == wanted {
      value = substr($0, index($0, "=") + 1)
      gsub(/^[[:space:]'\''"]+|[[:space:]'\''"]+$/, "", value)
      print value
      exit
    }
  ' "${env_file}"
}

image_ref="$(read_artifact_value BOOKING360_BACKEND_IMAGE)"
registry_username="$(read_artifact_value BOOKING360_BACKEND_GHCR_USERNAME)"
registry_token="$(read_artifact_value BOOKING360_BACKEND_GHCR_TOKEN)"
compose_project="$(read_artifact_value BOOKING360_BACKEND_COMPOSE_PROJECT)"
environment_name="$(read_artifact_value BOOKING360_BACKEND_ENVIRONMENT)"
network_alias="$(read_artifact_value BOOKING360_BACKEND_NETWORK_ALIAS)"
compose_project="${compose_project:-$(basename "${artifact_dir}")}"

case "${environment_name}" in
  staging)
    expected_project="booking360-backend-staging"
    expected_alias="booking360-backend-staging"
    ;;
  production)
    expected_project="booking360-backend-production"
    expected_alias="booking360-backend"
    ;;
  *)
    echo "The backend environment is not in the locked allowlist." >&2
    exit 1
    ;;
esac

[[ "${compose_project}" == "${expected_project}" ]] || {
  echo "The Compose project does not match the locked environment." >&2
  exit 1
}
[[ "${network_alias}" == "${expected_alias}" ]] || {
  echo "The backend network alias does not match the locked environment." >&2
  exit 1
}

case "${image_ref}" in
  ghcr.io/v-viebal/booking360-backend@sha256:*)
    expected_repo_digest="${image_ref}"
    ;;
  *)
    echo "Booking360 backend image must use the private GHCR repository at an exact digest." >&2
    exit 1
    ;;
esac

docker_config="$(mktemp -d)"
compose_dir="$(mktemp -d)"
cleanup() {
  docker --config "${docker_config}" logout ghcr.io >/dev/null 2>&1 || true
  rm -rf "${docker_config}" "${compose_dir}"
}
trap cleanup EXIT

: "${registry_username:?Temporary GHCR username is missing}"
: "${registry_token:?Temporary GHCR token is missing}"
printf '%s' "${registry_token}" |
  docker --config "${docker_config}" login ghcr.io \
    --username "${registry_username}" --password-stdin >/dev/null
docker --config "${docker_config}" pull "${image_ref}" >/dev/null

docker image inspect "${image_ref}" \
  --format '{{range .RepoDigests}}{{println .}}{{end}}' |
  grep -Fx -- "${expected_repo_digest}" >/dev/null || {
    echo "Pulled image RepoDigests do not contain the requested release." >&2
    exit 1
  }

# Do not make the GitHub token available to the application container.
sed -i \
  '/^BOOKING360_BACKEND_GHCR_USERNAME=/d;/^BOOKING360_BACKEND_GHCR_TOKEN=/d' \
  "${env_file}"
unset BOOKING360_BACKEND_GHCR_USERNAME BOOKING360_BACKEND_GHCR_TOKEN || true
export BOOKING360_BACKEND_IMAGE="${image_ref}"
export BOOKING360_BACKEND_PULL_POLICY=never

compose_version="${COOLIFY_COMPOSE_VERSION:-v2.31.0}"
compose_sha256="${COOLIFY_COMPOSE_SHA256:-8b5d2cb358427e654ada217cfdfedc00c4273f7a8ee07f27003a18d15461b6cd}"
compose_bin="${compose_dir}/docker-compose-linux-x86_64"
curl --fail --silent --show-error --location \
  --retry 5 --retry-all-errors --retry-delay 3 \
  "https://github.com/docker/compose/releases/download/${compose_version}/docker-compose-linux-x86_64" \
  --output "${compose_bin}"
printf '%s  %s\n' "${compose_sha256}" "${compose_bin}" | sha256sum -c -
chmod +x "${compose_bin}"

"${compose_bin}" \
  -p "${compose_project}" \
  --project-directory "${artifact_dir}" \
  --env-file "${env_file}" \
  -f "${compose_file}" \
  up -d --force-recreate --remove-orphans

container_id="$(
  docker ps -q \
    --filter "label=com.docker.compose.project=${compose_project}" \
    --filter "label=com.docker.compose.service=backend" |
    head -n 1
)"
[[ -n "${container_id}" ]] || {
  echo "The backend container was not created by the expected Compose project." >&2
  exit 1
}

if ! docker inspect "${container_id}" \
  --format '{{range .NetworkSettings.Networks}}{{range .Aliases}}{{println .}}{{end}}{{end}}' |
  grep -Fx -- "${network_alias}" >/dev/null; then
  docker network disconnect coolify "${container_id}" || true
  docker network connect --alias "${network_alias}" coolify "${container_id}"
fi

runtime_image="$(docker inspect "${container_id}" --format '{{.Config.Image}}')"
runtime_image_id="$(docker inspect "${container_id}" --format '{{.Image}}')"
runtime_repo_digests="$(
  docker inspect "${container_id}" \
    --format '{{range .RepoDigests}}{{println .}}{{end}}'
)"
[[ "${runtime_image}" == "${image_ref}" ]] || {
  echo "The running backend container does not use the requested exact image." >&2
  exit 1
}
grep -Fx -- "${expected_repo_digest}" <<<"${runtime_repo_digests}" >/dev/null || {
  echo "The running backend container RepoDigests do not match the requested release." >&2
  exit 1
}

# These value-safe markers are consumed by the deployment workflow to prove
# that Coolify's target host ran the requested immutable image.
printf 'runtime_image_ref=%s runtime_image_id=%s repo_digest_match=true\n' \
  "${runtime_image}" "${runtime_image_id}"
