#!/usr/bin/env bash
# 参照ソースがラッパーのビルドに混ざらないことを検査する。
set -euo pipefail
cd "$(dirname "$0")/.."
source_dirs=()
for directory in src tests; do
    if [[ -d "$directory" ]]; then
        source_dirs+=("$directory")
    fi
done
build_files=(Directory.Build.props)
shopt -s nullglob
build_files+=(./*.slnx ./*.sln ./*.targets)
check_references() {
    local result=0
    grep "$@" || result=$?
    if [[ "$result" -gt 1 ]]; then
        echo 'Reference isolation scan failed.' >&2
        exit "$result"
    fi
    return "$result"
}
if [[ "${#source_dirs[@]}" -gt 0 ]] &&
    check_references -REin 'Implem\.|_reference' "${source_dirs[@]}" --include='*.csproj' --include='*.cs'; then
    echo 'Reference source must not be imported into the wrapper.' >&2
    exit 1
fi
if check_references -Ein 'Implem\.|_reference' "${build_files[@]}"; then
    echo 'Reference source must not be included in the solution.' >&2
    exit 1
fi
if [[ -n "${PLEASANTER_REFERENCE_DIR:-}" ]]; then
    reference_dir="$PLEASANTER_REFERENCE_DIR"
    if command -v cygpath > /dev/null 2>&1; then
        reference_dir="$(cygpath -am "$reference_dir")"
    fi
    reference_dir="${reference_dir//\\//}"
    if [[ "$reference_dir" != /* && ! "$reference_dir" =~ ^[A-Za-z]:/ ]]; then
        reference_dir="$PWD/$reference_dir"
    fi
    reference_dir="${reference_dir%/}"
    if [[ -z "$reference_dir" ]]; then
        echo 'PLEASANTER_REFERENCE_DIR must identify a reference directory, not the filesystem root.' >&2
        exit 1
    fi
    patterns=(-e "$reference_dir" -e "${reference_dir//\//\\}")
    if command -v cygpath > /dev/null 2>&1; then
        posix_reference="$(cygpath -au "$reference_dir")"
        patterns+=(-e "$posix_reference" -e "${posix_reference//\//\\}")
    fi
    if check_references -Fin "${patterns[@]}" "${build_files[@]}" ||
        { [[ "${#source_dirs[@]}" -gt 0 ]] &&
            check_references -RFin "${patterns[@]}" "${source_dirs[@]}" --include='*.csproj' --include='*.cs'; }; then
        echo 'Shared reference source must not be imported into the wrapper.' >&2
        exit 1
    fi
fi
echo 'Reference isolation check passed.'
