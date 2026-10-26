#!/usr/bin/env bash
set -euo pipefail
shopt -s inherit_errexit

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source <(sed -n '/^function check_parameter_file() {$/,/^}$/p' "$repo_root/ism7mqtt/run.sh")

test_dir="$(mktemp -d)"
trap 'rm -rf -- "$test_dir"' EXIT

function bashio::log.info() { echo "$*"; }
function bashio::log.error() { echo "ERROR: $*" >&2; }

assert_valid() {
    local file="$1"
    local expected_count="$2"
    local expected_warning="$3"
    local output

    output="$(check_parameter_file "$file")"
    grep -Fq "contains $expected_count parameters." <<< "$output"
    if [[ "$expected_warning" == yes ]]; then
        grep -Fq 'WARNING WARNING WARNING' <<< "$output"
    elif grep -Fq 'WARNING WARNING WARNING' <<< "$output"; then
        echo "Unexpected parameter warning for $file." >&2
        return 1
    fi
}

assert_invalid() {
    local name="$1"
    local json="$2"
    local file="$test_dir/$name.json"
    local output="$test_dir/$name.log"

    printf '%s\n' "$json" > "$file"
    if check_parameter_file "$file" > "$output" 2>&1; then
        echo "Invalid parameter file was accepted: $name." >&2
        return 1
    fi
    grep -Fq "ERROR: Cannot read parameter file $file." "$output"
    if grep -Fq 'contains ' "$output"; then
        echo "Invalid parameter file produced a success message: $name." >&2
        return 1
    fi
}

cat > "$test_dir/inline parameters.json" <<'EOF'
{
  "Devices": [
    {"ReadBusAddress":"0x35","WriteBusAddress":"0x30","DeviceTemplateId":220000,"Parameter":[220022,220028,220042,220045,220046,220095,220103,220126,220134]},
    {"ReadBusAddress":"0x35","WriteBusAddress":"0x30","DeviceTemplateId":340000,"Parameter":[340001,340004,340005,340006,340007,340008,340009,340011,340012,340013,340014,340015,340016,340026,340029,340031,340032,340033,340035,340040,340051,340052,340053,340054,340055]},
    {"ReadBusAddress":"0x35","WriteBusAddress":"0x30","DeviceTemplateId":350000,"Parameter":[350001,350004,350009,350012,350014,350015]},
    {"ReadBusAddress":"0x8","WriteBusAddress":"0x3","DeviceTemplateId":240000,"Parameter":[240000,240001,240002,240003,240004,240005,240006,240008,240009,240017,240021,240025,240026,240032,240035,240036,240039,240040,240041,240042,240043,240044,240046,240047,240048,240049,240050,240051,240052,240053,240054,240058,240059,240060,240061,240066,240074,240076,240077,240033,240078,240079,240080,240081,240082,240084,240085,240086,240087,240088,240089,240090,240091,240092,240093,240094,240095,240096,240098,240099,240100,240103,240104,240105,240102,240106,240107,240108]}
  ]
}
EOF

jq . "$test_dir/inline parameters.json" > "$test_dir/pretty.json"
jq -c . "$test_dir/inline parameters.json" > "$test_dir/compact.json"
sed 's/$/\r/' "$test_dir/inline parameters.json" > "$test_dir/crlf.json"
for file in "inline parameters" pretty compact crlf; do
    assert_valid "$test_dir/$file.json" 108 no
done

printf '%s\n' '{"Devices":[]}' > "$test_dir/no-devices.json"
printf '%s\n' '{"Devices":[{"Parameter":[]}]}' > "$test_dir/no-parameters.json"
assert_valid "$test_dir/no-devices.json" 0 no
assert_valid "$test_dir/no-parameters.json" 0 no

for count in 150 151; do
    jq -nc --argjson count "$count" '{Devices:[{Parameter:[range(0;$count)]}]}' > "$test_dir/$count.json"
done
assert_valid "$test_dir/150.json" 150 no
assert_valid "$test_dir/151.json" 151 yes

assert_invalid truncated '{"Devices":['
assert_invalid trailing-comma '{"Devices":[{"Parameter":[240108,]}]}'
assert_invalid empty ''
assert_invalid multiple-documents '{"Devices":[]} {"Devices":[]}'
assert_invalid missing-devices '{}'
assert_invalid object-devices '{"Devices":{}}'
assert_invalid missing-parameters '{"Devices":[{}]}'
assert_invalid null-parameters '{"Devices":[{"Parameter":null}]}'
assert_invalid object-parameters '{"Devices":[{"Parameter":{}}]}'
assert_invalid string-parameters '{"Devices":[{"Parameter":"240108"}]}'

if check_parameter_file "$test_dir/missing.json" > "$test_dir/missing.log" 2>&1; then
    echo "Missing parameter file was accepted." >&2
    exit 1
fi
grep -Fq "ERROR: Cannot read parameter file $test_dir/missing.json." "$test_dir/missing.log"

echo "Parameter file tests passed."
