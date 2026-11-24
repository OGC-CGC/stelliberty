#!/usr/bin/env bash
set -euo pipefail

release_channel="$1"
for name in TELEGRAM_BOT_API TELEGRAM_BOT_TOKEN TELEGRAM_CHAT_ID; do
  if [[ -z "${!name:-}" ]]; then
    echo "::error::Missing GitHub Actions secret ${name}."
    exit 1
  fi
done

bot_api="${TELEGRAM_BOT_API%/}"
if [[ ! "$bot_api" =~ ^https?://[^[:space:]]+/bot$ ]]; then
  echo '::error::TELEGRAM_BOT_API must use the format https://example.com/bot.'
  exit 1
fi

form=(--form-string "chat_id=${TELEGRAM_CHAT_ID}")
reply_to="${TELEGRAM_REPLY_TO_MESSAGE_ID:-}"
if [[ -n "$reply_to" ]]; then
  if [[ ! "$reply_to" =~ ^[1-9][0-9]*$ ]]; then
    echo '::error::TELEGRAM_REPLY_TO_MESSAGE_ID must be a positive integer.'
    exit 1
  fi
  reply_parameters="$(jq -cn --argjson id "$reply_to" '{message_id: $id}')"
  form+=(--form-string "reply_parameters=${reply_parameters}")
fi

release="$(gh release view "$RELEASE_TAG" --repo "$GITHUB_REPOSITORY" --json name,body,url,isDraft)"
if ! jq -e '.isDraft == false' >/dev/null <<< "$release"; then
  echo "::error::Release ${RELEASE_TAG} is not published."
  exit 1
fi

caption="$(jq -r --arg channel "$release_channel" '
  "\(.name) (\($channel))\n\(.url)" as $header
  | ((.body // "")
      | gsub(" \\[\u0060[0-9a-f]+\u0060\\]\\(https?://[^)]+/commit/[0-9a-f]+\\)"; "")
      | sub("^## Changes\\r?\\n\\r?\\n"; "")) as $notes
  | (1000 - ($header | length) - 2) as $budget
  | if $notes == "" then $header
    elif ($notes | length) <= $budget then "\($header)\n\n\($notes)"
    else "\($header)\n\n\($notes[0:($budget - 1)])…"
    end
' <<< "$release")"

asset_dir="$(mktemp -d "${RUNNER_TEMP}/telegram-release.XXXXXX")"
gh release download "$RELEASE_TAG" --repo "$GITHUB_REPOSITORY" --dir "$asset_dir" \
  --pattern '*.zip' --pattern '*.exe' --pattern '*.AppImage' --pattern '*.dmg'

shopt -s nullglob
assets=("$asset_dir"/*)
asset_count="${#assets[@]}"
if (( asset_count == 0 || asset_count > 10 )); then
  echo "::error::Expected 1 to 10 release assets for Telegram, found ${asset_count}."
  exit 1
fi

if (( asset_count == 1 )); then
  method=sendDocument
  form+=(--form "document=@${assets[0]}" --form-string "caption=${caption}")
else
  method=sendMediaGroup
  for index in "${!assets[@]}"; do
    form+=(--form "file${index}=@${assets[index]}")
  done
  media="$(jq -cn --argjson count "$asset_count" --arg caption "$caption" '
    [range($count) | {type: "document", media: "attach://file\(.)"}
      + if . == 0 then {caption: $caption} else {} end]
  ')"
  form+=(--form-string "media=${media}")
fi

response="$(curl --silent --show-error --connect-timeout 30 --max-time 1200 \
  "${form[@]}" "${bot_api}${TELEGRAM_BOT_TOKEN}/${method}")"
if ! jq -e '.ok == true' >/dev/null <<< "$response"; then
  echo '::error::Telegram did not accept the release assets.'
  jq -r '.description // empty' <<< "$response" >&2
  exit 1
fi

echo "Sent ${asset_count} release asset(s) to Telegram."
