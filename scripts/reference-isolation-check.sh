#!/usr/bin/env bash
# 参照ソースがラッパーのビルドに混ざらないことを検査する。
set -euo pipefail
cd "$(dirname "$0")/.."
if grep -REin 'Implem\.|_reference' src tests --include='*.csproj' --include='*.cs'; then
    echo 'Reference source must not be imported into the wrapper.' >&2
    exit 1
fi
if grep -Ein 'Implem\.|_reference' ./*.slnx Directory.Build.props; then
    echo 'Reference source must not be included in the solution.' >&2
    exit 1
fi
echo 'Reference isolation check passed.'
