#!/usr/bin/env bash
# Isolated Linux integration services. Test credentials are only valid in these containers.
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
run="mailserver-storage-test-$(date +%s)-$$"
mysql_image="${MYSQL_TEST_IMAGE:-mysql:8.4}"
s3_image="${S3_TEST_IMAGE:-minio/minio:RELEASE.2025-04-22T22-12-26Z}"
sdk_image="${DOTNET_TEST_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}"
cleanup() {
  docker rm -fv "$run-sdk" "$run-mysql" "$run-s3" >/dev/null 2>&1 || true
  docker network rm "$run" >/dev/null 2>&1 || true
  docker volume rm "$run-work" >/dev/null 2>&1 || true
}
trap cleanup EXIT
docker pull "$mysql_image"
docker pull "$s3_image"
docker pull "$sdk_image"
docker network create "$run" >/dev/null
docker volume create "$run-work" >/dev/null
docker run -d --name "$run-mysql" --network "$run" --network-alias db \
  -e MYSQL_ROOT_PASSWORD=mailserver-test-secret -e MYSQL_DATABASE=mailserver_acceptance \
  "$mysql_image" >/dev/null
docker run -d --name "$run-s3" --network "$run" --network-alias storage \
  -e MINIO_ROOT_USER=mailserver-test -e MINIO_ROOT_PASSWORD=mailserver-test-secret \
  "$s3_image" server /data >/dev/null
ready=false
for ((i=0; i<90; i++)); do
  if docker exec "$run-mysql" mysqladmin ping -h 127.0.0.1 --protocol=tcp -pmailserver-test-secret --silent >/dev/null 2>&1; then
    ready=true; break
  fi
  sleep 2
done
if [[ "$ready" != true ]]; then echo 'MySQL startup timed out.' >&2; exit 1; fi
docker run --rm --name "$run-sdk" --network "$run" \
  -v "$repo:/repo:ro" -v "$run-work:/work" -w /work \
  -e 'MAILSERVER_TEST_MYSQL_CONNECTION=Server=db;Database=mailserver_acceptance;User ID=root;Password=mailserver-test-secret;SslMode=Disabled' \
  -e MAILSERVER_TEST_S3_URL=http://storage:9000 \
  "$sdk_image" bash -lc '
set -euo pipefail
for ((i=0; i<60; i++)); do
  if curl --fail --silent http://storage:9000/minio/health/ready >/dev/null; then break; fi
  sleep 2
done
curl --fail --silent http://storage:9000/minio/health/ready >/dev/null
tar -C /repo --exclude=node_modules --exclude=bin --exclude=obj --exclude=builds -cf - src tests Directory.Build.props Directory.Packages.props MailServer.slnx | tar -xf -
dotnet test tests/MailServer.Tests/MailServer.Tests.csproj -c Release -p:SkipAdminBuild=true --filter "FullyQualifiedName~S3IntegrationTests|FullyQualifiedName~DistributedStorageTests"
'
