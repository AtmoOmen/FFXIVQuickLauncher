#!/usr/bin/env bash

set -euo pipefail

: "${CNB_TOKEN:?CNB_TOKEN is required}"
: "${GH_TOKEN:?GH_TOKEN is required}"
: "${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required}"

readonly API_BASE="https://api.cnb.cool"
readonly CNB_REPOSITORY="atmoomen/xivlauncher-distribute"
readonly CNB_RELEASE_BASE_URL="https://cnb.cool/$CNB_REPOSITORY/-/releases/download"
readonly PACKAGE_ID="XIVLauncherCN"
readonly WORK_DIR="cnb_backfill_work"
readonly MAX_VERSIONS="${MAX_VERSIONS:-10}"

if ! [[ "$MAX_VERSIONS" =~ ^[1-9][0-9]*$ ]]; then
  echo "::error::MAX_VERSIONS must be a positive integer"
  exit 1
fi

mkdir -p "$WORK_DIR"
: > "$WORK_DIR/assets.ndjson"

mapfile -t release_tags < <(
  gh release list \
    --repo "$GITHUB_REPOSITORY" \
    --limit "$MAX_VERSIONS" \
    --json tagName \
    --jq '.[].tagName'
)

if [ "${#release_tags[@]}" -eq 0 ]; then
  echo "::error::No GitHub releases found"
  exit 1
fi

if [ -n "${RELEASE_TAG:-}" ]; then
  requested_tag_found=false
  for tag in "${release_tags[@]}"; do
    if [ "$tag" = "$RELEASE_TAG" ]; then
      requested_tag_found=true
      break
    fi
  done

  if [ "$requested_tag_found" != true ]; then
    echo "::error::Requested release $RELEASE_TAG is outside the latest $MAX_VERSIONS releases"
    exit 1
  fi
fi

latest_tag="${release_tags[0]}"
asset_count=0

for tag_index in "${!release_tags[@]}"; do
  tag="${release_tags[$tag_index]}"
  tag_dir="$WORK_DIR/$tag"
  mkdir -p "$tag_dir"

  echo "::group::Download GitHub release $tag"
  if ! gh release download "$tag" \
    --repo "$GITHUB_REPOSITORY" \
    --pattern '*.nupkg' \
    --dir "$tag_dir"; then
    echo "::warning::Release $tag has no nupkg assets"
    echo "::endgroup::"
    continue
  fi

  if ! gh release download "$tag" \
    --repo "$GITHUB_REPOSITORY" \
    --pattern 'releases.win.json' \
    --dir "$tag_dir"; then
    echo "::warning::Release $tag has no releases.win.json"
  fi
  echo "::endgroup::"

  shopt -s nullglob
  nupkg_files=("$tag_dir"/*.nupkg)
  shopt -u nullglob

  for nupkg_file in "${nupkg_files[@]}"; do
    file_name="$(basename -- "$nupkg_file")"
    if [[ "$file_name" =~ ^XIVLauncherCN-(.+)-(full|delta)\.nupkg$ ]]; then
      version="${BASH_REMATCH[1]}"
      package_type="${BASH_REMATCH[2]}"
    else
      echo "::error::Cannot parse package metadata from $file_name"
      exit 1
    fi

    if [ "$version" != "${tag#v}" ]; then
      echo "::error::Package $file_name does not belong to release $tag"
      exit 1
    fi

    if [ "$package_type" = "full" ]; then
      package_type="Full"
      type_order=0
    else
      package_type="Delta"
      type_order=1
    fi

    sha1="$(sha1sum "$nupkg_file" | awk '{print toupper($1)}')"
    sha256="$(sha256sum "$nupkg_file" | awk '{print toupper($1)}')"
    size="$(stat -c%s "$nupkg_file")"
    source_entry='{}'

    if [ -s "$tag_dir/releases.win.json" ]; then
      source_entry="$(jq -c --arg file "$file_name" \
        'first(.Assets[] | select((.FileName | split("/") | last) == $file)) // {}' \
        "$tag_dir/releases.win.json")"
    fi

    jq -nc \
      --argjson source "$source_entry" \
      --arg package_id "$PACKAGE_ID" \
      --arg version "$version" \
      --arg type "$package_type" \
      --arg file_name "$CNB_RELEASE_BASE_URL/$tag/$file_name" \
      --arg sha1 "$sha1" \
      --arg sha256 "$sha256" \
      --argjson size "$size" \
      --argjson order "$tag_index" \
      --argjson type_order "$type_order" \
      '$source + {
        PackageId: $package_id,
        Version: $version,
        Type: $type,
        FileName: $file_name,
        SHA1: $sha1,
        SHA256: $sha256,
        Size: $size,
        _Order: $order,
        _TypeOrder: $type_order
      }' >> "$WORK_DIR/assets.ndjson"

    asset_count=$((asset_count + 1))
  done
done

if [ "$asset_count" -eq 0 ]; then
  echo "::error::No nupkg assets collected"
  exit 1
fi

jq -s \
  '{Assets: (sort_by([._Order, ._TypeOrder]) | map(del(._Order, ._TypeOrder)))}' \
  "$WORK_DIR/assets.ndjson" > "$WORK_DIR/releases.win.json"

jq -r '.Assets[] | "\(.SHA1) \(.FileName) \(.Size)"' \
  "$WORK_DIR/releases.win.json" > "$WORK_DIR/RELEASES"

echo "Collected $asset_count packages from ${#release_tags[@]} GitHub releases"

readonly AUTH_HEADER="Authorization: Bearer $CNB_TOKEN"
readonly ACCEPT_HEADER="Accept: application/vnd.cnb.api+json"

upload_asset() {
  local release_id="$1"
  local asset_file="$2"
  local asset_name
  local asset_size
  local upload_payload
  local upload_response_file="$WORK_DIR/upload-response.json"
  local upload_http
  local upload_url
  local verify_url
  local verify_suffix
  local upload_token
  local asset_path
  local put_http
  local confirmation_response_file="$WORK_DIR/upload-confirmation.json"
  local confirmation_http

  asset_name="$(basename -- "$asset_file")"
  asset_size="$(stat -c%s "$asset_file")"
  upload_payload="$(jq -nc \
    --arg name "$asset_name" \
    --argjson size "$asset_size" \
    '{asset_name: $name, size: $size, overwrite: true}')"

  upload_http="$(curl -sS \
    -o "$upload_response_file" \
    -w '%{http_code}' \
    -X POST \
    -H "$AUTH_HEADER" \
    -H "$ACCEPT_HEADER" \
    -H 'Content-Type: application/json' \
    -d "$upload_payload" \
    "$API_BASE/$CNB_REPOSITORY/-/releases/$release_id/asset-upload-url")"

  if [[ "$upload_http" != 2* ]]; then
    echo "::error::Failed to obtain upload URL for $asset_name (HTTP $upload_http)"
    sed -n '1,40p' "$upload_response_file"
    exit 1
  fi

  upload_url="$(jq -er '.upload_url' "$upload_response_file")"
  verify_url="$(jq -er '.verify_url' "$upload_response_file")"
  verify_suffix="${verify_url#*/asset-upload-confirmation/}"

  if [ "$verify_suffix" = "$verify_url" ] || [[ "$verify_suffix" != */* ]]; then
    echo "::error::Invalid upload confirmation URL for $asset_name"
    exit 1
  fi

  upload_token="${verify_suffix%%/*}"
  asset_path="${verify_suffix#*/}"
  put_http="$(curl -sS \
    -o /dev/null \
    -w '%{http_code}' \
    -X PUT \
    -T "$asset_file" \
    -H "$AUTH_HEADER" \
    "$upload_url")"

  if [[ "$put_http" != 2* ]]; then
    echo "::error::Failed to upload $asset_name (HTTP $put_http)"
    exit 1
  fi

  confirmation_http="$(curl -sS \
    -o "$confirmation_response_file" \
    -w '%{http_code}' \
    -X POST \
    -H "$AUTH_HEADER" \
    -H "$ACCEPT_HEADER" \
    -H 'Content-Type: application/json' \
    "$API_BASE/$CNB_REPOSITORY/-/releases/$release_id/asset-upload-confirmation/$upload_token/$asset_path")"

  if [[ "$confirmation_http" != 2* ]]; then
    echo "::error::Failed to confirm $asset_name (HTTP $confirmation_http)"
    sed -n '1,40p' "$confirmation_response_file"
    exit 1
  fi

  echo "Uploaded $asset_name"
}

# --------------------------------------------
# 修改部分：Publish CNB release 循环
# --------------------------------------------
for tag in "${release_tags[@]}"; do
  tag_dir="$WORK_DIR/$tag"
  shopt -s nullglob
  nupkg_files=("$tag_dir"/*.nupkg)
  shopt -u nullglob

  if [ "${#nupkg_files[@]}" -eq 0 ]; then
    continue
  fi

  echo "::group::Publish CNB release $tag"
  existing_release="$(curl -fsS \
    -H "$AUTH_HEADER" \
    -H "$ACCEPT_HEADER" \
    "$API_BASE/$CNB_REPOSITORY/-/releases/tags/$tag" || true)"

  # MODIFIED: 如果 CNB 上已存在同名 release，则跳过（不删除，不重建）
  if [ -n "$existing_release" ]; then
    echo "CNB release $tag already exists, skipping upload"
    echo "::endgroup::"
    continue
  fi

  # 如果不存在，才创建新 release 并上传 assets
  if [ "$tag" = "$latest_tag" ]; then
    make_latest=true
  else
    make_latest=false
  fi

  release_payload="$(jq -nc \
    --arg tag "$tag" \
    --arg make_latest "$make_latest" \
    '{
      tag_name: $tag,
      name: $tag,
      body: ("Release " + $tag),
      target_commitish: "master",
      prerelease: false,
      make_latest: $make_latest
    }')"
  release_response_file="$WORK_DIR/release-response.json"
  release_http="$(curl -sS \
    -o "$release_response_file" \
    -w '%{http_code}' \
    -X POST \
    -H "$AUTH_HEADER" \
    -H "$ACCEPT_HEADER" \
    -H 'Content-Type: application/json' \
    -d "$release_payload" \
    "$API_BASE/$CNB_REPOSITORY/-/releases")"

  if [[ "$release_http" != 2* ]]; then
    echo "::error::Failed to create CNB release $tag (HTTP $release_http)"
    sed -n '1,40p' "$release_response_file"
    exit 1
  fi

  release_id="$(jq -er '.id' "$release_response_file")"
  for nupkg_file in "${nupkg_files[@]}"; do
    upload_asset "$release_id" "$nupkg_file"
  done
  echo "::endgroup::"
done

# --------------------------------------------
# 以下部分保持不变：更新 CNB 仓库元数据文件
# --------------------------------------------
readonly CNB_GIT_URL="https://cnb.cool/$CNB_REPOSITORY.git"
readonly CNB_PUSH_URL="https://cnb:$CNB_TOKEN@cnb.cool/$CNB_REPOSITORY.git"
readonly CNB_REPO_DIR="$WORK_DIR/cnb-repo"

git clone --depth 1 "$CNB_GIT_URL" "$CNB_REPO_DIR"
cp "$WORK_DIR/releases.win.json" "$WORK_DIR/RELEASES" "$CNB_REPO_DIR/"
printf '%s' "$latest_tag" > "$CNB_REPO_DIR/RELEASE"

git -C "$CNB_REPO_DIR" config user.name "github-actions[bot]"
git -C "$CNB_REPO_DIR" config user.email "github-actions[bot]@users.noreply.github.com"
git -C "$CNB_REPO_DIR" add RELEASE releases.win.json RELEASES

if ! git -C "$CNB_REPO_DIR" diff --cached --quiet; then
  git -C "$CNB_REPO_DIR" commit -m "release: $latest_tag"
  git -C "$CNB_REPO_DIR" push "$CNB_PUSH_URL" HEAD:master
fi

# --------------------------------------------
# 清理过时 CNB release（保持不变）
# --------------------------------------------
declare -A keep_tags=()
for tag in "${release_tags[@]}"; do
  keep_tags["$tag"]=true
done

curl -fsS \
  -H "$AUTH_HEADER" \
  -H "$ACCEPT_HEADER" \
  "$API_BASE/$CNB_REPOSITORY/-/releases?page_size=100" \
  > "$WORK_DIR/cnb-releases.json"

mapfile -t cnb_release_tags < <(
  jq -r '.[].tag_name' "$WORK_DIR/cnb-releases.json"
)

for tag in "${cnb_release_tags[@]}"; do
  if [[ "$tag" =~ ^[0-9]+([.][0-9]+){2}([-+][0-9A-Za-z.-]+)?$ ]] && \
    [ -z "${keep_tags[$tag]+x}" ]; then
    echo "Deleting stale CNB release $tag"
    stale_release="$(curl -fsS \
      -H "$AUTH_HEADER" \
      -H "$ACCEPT_HEADER" \
      "$API_BASE/$CNB_REPOSITORY/-/releases/tags/$tag")"
    stale_release_id="$(jq -er '.id' <<< "$stale_release")"
    curl -fsS \
      -X DELETE \
      -H "$AUTH_HEADER" \
      -H "$ACCEPT_HEADER" \
      "$API_BASE/$CNB_REPOSITORY/-/releases/$stale_release_id"
  fi
done

echo "CNB backfill complete: $asset_count packages, ${#release_tags[@]} releases"