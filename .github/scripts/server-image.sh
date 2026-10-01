#!/usr/bin/env bash
# Tags of the published server image, ghcr.io/atlitech/reports-server, for the workflows that publish
# it: release.yml (a new release), server-image-refresh.yml (the newest release rebuilt with a newer
# pinned chrome-headless-shell), and server-image-publish.yml, which both call.
#
#   server-image.sh release-tags <version> <prerelease>
#       Print the tags a release moves: <version>; <major>.<minor> unless the version has a
#       pre-release suffix such as -preview.1; latest only when it has none and <prerelease> (the
#       GitHub release's flag, true or false) is false. Fails for a tag that is not such a version.
#   server-image.sh index [--denied-is-absent] <image>:<tag>
#       Print "<index digest> <chrome version>" for an image in the registry, with - for an image
#       that does not record its Chrome version, or nothing when the tag does not exist.
#   server-image.sh refresh-plan
#       Choose the release a Chrome refresh rebuilds. Reads "<tag><TAB><prerelease>" lines, newest
#       release first, and picks the first one that follows the release tag convention and has an
#       image (or only REQUESTED, when set). Refreshes only when CHROME_VERSION is newer than the
#       Chrome version that image records. Environment: IMAGE, CHROME_VERSION, REQUESTED.
#   server-image.sh push-manifest <image>@<digest>...
#       Combine the per-architecture images into a multi-arch index and push it as
#       <VERSION>-chrome<CHROME_VERSION>, which never moves to a different image, and as each tag in
#       TAGS. With ONLY_MOVE_FROM set to an index digest, a tag in TAGS moves only if it points at
#       that index now, so a refresh never moves latest or <major>.<minor> away from a newer
#       release. Environment: IMAGE, VERSION, CHROME_VERSION, TAGS, ONLY_MOVE_FROM, REVISION,
#       SOURCE, IMAGE_TITLE, IMAGE_DESCRIPTION, IMAGE_LICENSES.
#
# refresh-plan and push-manifest write their results to $GITHUB_OUTPUT, or to stdout outside Actions.
set -euo pipefail

script_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# The index annotation (and image label) that records the chrome-headless-shell version.
chrome_annotation=io.github.atlitech.reports.chrome-version
release_pattern='^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?$'
tag_pattern='^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$'
digest_pattern='^sha256:[0-9a-f]{64}$'

fail() {
  echo "::error::$*" >&2
  exit 1
}

usage() {
  sed -n '6,26s/^# \{0,1\}//p' "${BASH_SOURCE[0]}" >&2
  exit 2
}

output() {
  echo "$1=$2" >>"${GITHUB_OUTPUT:-/dev/stdout}"
}

release_tags() {
  local version="$1" prerelease="$2" tags
  [[ "$prerelease" =~ ^(true|false)$ ]] || fail "The pre-release flag is '$prerelease', not true or false."
  [[ "$version" =~ $release_pattern ]] ||
    fail "Release tag $version is not a version such as 0.26.0 or 0.26.0-preview.1."
  tags="$version"
  if [ -z "${BASH_REMATCH[4]}" ]; then
    tags="$tags ${BASH_REMATCH[1]}.${BASH_REMATCH[2]}"
    if [ "$prerelease" = false ]; then
      tags="$tags latest"
    fi
  fi
  echo "$tags"
}

index() {
  local absent='not found|manifest unknown|name unknown'
  if [ "${1:-}" = --denied-is-absent ]; then
    absent="$absent|denied"
    shift
  fi
  local reference="$1" errors manifest digest chrome
  errors="$(mktemp)"
  if ! manifest="$(docker buildx imagetools inspect "$reference" --format '{{json .Manifest}}' 2>"$errors")"; then
    if grep -qiE "$absent" "$errors"; then
      echo "No image at $reference: $(tr '\n' ' ' <"$errors")" >&2
      rm -f "$errors"
      return 0
    fi
    cat "$errors" >&2
    rm -f "$errors"
    fail "Could not inspect $reference."
  fi
  rm -f "$errors"
  digest="$(jq -r .digest <<<"$manifest")"
  [[ "$digest" =~ $digest_pattern ]] || fail "Could not read the digest of $reference."
  # By digest, so both reads see the same index even if the tag moves in between.
  chrome="$(
    docker buildx imagetools inspect --raw "${reference%:*}@$digest" |
      jq -r --arg key "$chrome_annotation" '.annotations[$key] // "-"'
  )"
  echo "$digest $chrome"
}

# The manifests an image reference stands for: an index's entries (platform images and their
# attestations), or the single manifest itself.
manifest_digests() {
  local reference="$1" raw
  raw="$(docker buildx imagetools inspect --raw "$reference")"
  if jq -e 'has("manifests")' <<<"$raw" >/dev/null; then
    jq -r '.manifests[].digest' <<<"$raw"
  else
    echo "${reference##*@}"
  fi
}

refresh_plan() {
  : "${IMAGE:?}" "${CHROME_VERSION:?}"
  local requested="${REQUESTED:-}" tag prerelease tags="" info="" digest current comparison
  while IFS=$'\t' read -r tag prerelease; do
    [ -n "$tag" ] || continue
    if [ -n "$requested" ] && [ "$tag" != "$requested" ]; then
      continue
    fi
    if ! tags="$(release_tags "$tag" "$prerelease" 2>/dev/null)"; then
      echo "Skipping $tag: the release workflow publishes no image for a tag like it."
      continue
    fi
    info="$(index --denied-is-absent "$IMAGE:$tag")"
    if [ -n "$info" ]; then
      break
    fi
    echo "Skipping $tag: it has no image."
  done

  if [ -z "$info" ]; then
    if [ -n "$requested" ]; then
      fail "Release $requested has no image at $IMAGE:$requested to refresh."
    fi
    echo "::notice::No published release has an image yet, so there is nothing to refresh."
    output refresh false
    return
  fi

  read -r digest current <<<"$info"
  if [ "$current" = - ]; then
    echo "$IMAGE:$tag ($digest) does not record its Chrome version; refreshing it with $CHROME_VERSION."
  else
    comparison="$("$script_directory/chrome-headless-shell.sh" compare "$CHROME_VERSION" "$current")"
    case "$comparison" in
      newer) echo "$IMAGE:$tag ($digest) has Chrome $current; refreshing it with $CHROME_VERSION." ;;
      same)
        echo "::notice::$IMAGE:$tag already has Chrome $current, so there is nothing to refresh."
        output refresh false
        return
        ;;
      *)
        echo "::notice::$IMAGE:$tag has Chrome $current, newer than the pinned $CHROME_VERSION; tags never move back."
        output refresh false
        return
        ;;
    esac
  fi
  output refresh true
  output version "$tag"
  output tags "$tags"
  output only-move-from "$digest"
}

push_manifest() {
  : "${IMAGE:?}" "${VERSION:?}" "${CHROME_VERSION:?}" "${TAGS?}" "${REVISION:?}" "${SOURCE:?}"
  : "${IMAGE_TITLE:?}" "${IMAGE_DESCRIPTION:?}" "${IMAGE_LICENSES:?}"
  local only_move_from="${ONLY_MOVE_FROM:-}" immutable source info current tag digest new existing
  local -a sources=("$@") tags=() tag_arguments=()
  [ "${#sources[@]}" -gt 0 ] || fail "No images to combine."
  for source in "${sources[@]}"; do
    [[ "${source##*@}" =~ $digest_pattern ]] || fail "$source is not an image reference by digest."
  done
  if [ -n "$only_move_from" ] && [[ ! "$only_move_from" =~ $digest_pattern ]]; then
    fail "ONLY_MOVE_FROM is '$only_move_from', not a digest."
  fi

  immutable="$VERSION-chrome$CHROME_VERSION"
  [[ "$immutable" =~ $tag_pattern ]] || fail "$immutable is not a valid image tag."
  new="$(for source in "${sources[@]}"; do manifest_digests "$source"; done | sort)"
  info="$(index "$IMAGE:$immutable")"
  if [ -n "$info" ]; then
    read -r current _ <<<"$info"
    existing="$(manifest_digests "$IMAGE@$current" | sort)"
    if [ "$existing" != "$new" ]; then
      fail "$IMAGE:$immutable already exists ($current) as a different image, and that tag never moves."
    fi
    echo "$IMAGE:$immutable already has these images; pushing it again leaves it as it is."
  fi
  tags+=("$immutable")

  for tag in $TAGS; do
    [[ "$tag" =~ $tag_pattern ]] || fail "$tag is not a valid image tag."
    if [ "$tag" = "$immutable" ]; then
      continue
    fi
    if [ -n "$only_move_from" ]; then
      info="$(index "$IMAGE:$tag")"
      current="${info%% *}"
      if [ "$current" != "$only_move_from" ]; then
        echo "::warning::Leaving $IMAGE:$tag as it is: it points at ${current:-nothing}, not $only_move_from."
        continue
      fi
    fi
    tags+=("$tag")
  done

  for tag in "${tags[@]}"; do
    tag_arguments+=(--tag "$IMAGE:$tag")
  done
  echo "Tagging: ${tags[*]}"
  # The index annotations are what ghcr.io shows for a multi-arch image.
  docker buildx imagetools create "${tag_arguments[@]}" \
    --annotation "index:org.opencontainers.image.title=$IMAGE_TITLE" \
    --annotation "index:org.opencontainers.image.description=$IMAGE_DESCRIPTION" \
    --annotation "index:org.opencontainers.image.source=$SOURCE" \
    --annotation "index:org.opencontainers.image.url=$SOURCE" \
    --annotation "index:org.opencontainers.image.version=$VERSION" \
    --annotation "index:org.opencontainers.image.revision=$REVISION" \
    --annotation "index:org.opencontainers.image.licenses=$IMAGE_LICENSES" \
    --annotation "index:$chrome_annotation=$CHROME_VERSION" \
    "${sources[@]}"
  docker buildx imagetools inspect "$IMAGE:$immutable"

  info="$(index "$IMAGE:$immutable")"
  digest="${info%% *}"
  [[ "$digest" =~ $digest_pattern ]] || fail "Could not read the digest of $IMAGE:$immutable."
  output digest "$digest"
  output tags "${tags[*]}"
}

[ "$#" -ge 1 ] || usage
command="$1"
shift
case "$command" in
  release-tags) [ "$#" -eq 2 ] || usage; release_tags "$@" ;;
  index) [ "$#" -ge 1 ] && [ "$#" -le 2 ] || usage; index "$@" ;;
  refresh-plan) [ "$#" -eq 0 ] || usage; refresh_plan ;;
  push-manifest) [ "$#" -ge 1 ] || usage; push_manifest "$@" ;;
  *) usage ;;
esac
