#!/usr/bin/env bash
# Build the web UI, then start the API (which serves the UI) on http://localhost:5080
set -euo pipefail
cd "$(dirname "$0")/.."

if ! command -v node >/dev/null 2>&1; then
  echo "Node.js 20 or newer is needed once, to build the UI. Install it without sudo:" >&2
  echo "  curl -o- https://raw.githubusercontent.com/nvm-sh/nvm/v0.40.1/install.sh | bash" >&2
  echo "  (open a new terminal)  nvm install --lts" >&2
  exit 1
fi

(cd web && npm install --no-audit --no-fund && npm run build)
echo
echo "Open http://localhost:5080"
dotnet run --project src/DataLens.Api
