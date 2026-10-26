#!/usr/bin/with-contenv bashio

export CONFIG_PATH=/data/options.json

export DEBUG_LOGGING=$(bashio::config 'debug_logging')

export ISM7_STARTUP_TIMEOUT=90
if bashio::config.has_value 'startup_timeout'; then
    export ISM7_STARTUP_TIMEOUT="$(bashio::config 'startup_timeout')"
fi
export ISM7_SERIALIZE_PUSH_SUBSCRIBES=false
if bashio::config.has_value 'serialize_push_subscribes'; then
    export ISM7_SERIALIZE_PUSH_SUBSCRIBES="$(bashio::config 'serialize_push_subscribes')"
fi


export ISM7_MQTTHOST="$(bashio::config 'mqtt_host')"
export ISM7_MQTTPORT="$(bashio::config 'mqtt_port')"
export ISM7_MQTTUSERNAME="$(bashio::config 'mqtt_user')"
export ISM7_MQTTPASSWORD="$(bashio::config 'mqtt_password')"


if [[ "$ISM7_MQTTHOST" == "null" ]]; then
    export ISM7_MQTTHOST="$(bashio::services mqtt 'host')"
    export ISM7_MQTTPORT="$(bashio::services mqtt 'port')"
    export ISM7_MQTTUSERNAME="$(bashio::services mqtt 'username')"
    export ISM7_MQTTPASSWORD="$(bashio::services mqtt 'password')"
    echo "Reading config from MQTT broker add-on: $ISM7_MQTTHOST/$ISM7_MQTTUSERNAME"
else
    echo "Using config from add-on configuration: $ISM7_MQTTHOST/$ISM7_MQTTUSERNAME"
fi



function run_bridge_with_retries() {
    local retries=0

    while true; do
        echo "Starting $*" | ts
        "$@" 2>&1 | ts || echo "ism7mqtt unexpectedly quit with return code $?"

        if (( retries < 3 )); then
            retries=$((retries + 1))
            echo "ISM7 $ISM7_HOMEASSISTANT_ID ($ISM7_IP): retry $retries/3 in 10 seconds." | ts
            sleep 10
        else
            echo "ISM7 $ISM7_HOMEASSISTANT_ID ($ISM7_IP): initial attempt and 3 retries ended; waiting 5 minutes before trying again." | ts
            sleep 300
            retries=0
        fi
    done
}

function check_parameter_file() {
    local parameters="$1"
    local parameter_count

    if ! parameter_count="$(jq --slurp --exit-status '
        if length != 1 then
            error("Expected one JSON document")
        else
            .[0]
        end
        | if (.Devices | type) != "array" then
            error("Devices must be an array")
        else
            [.Devices[] |
                if (.Parameter | type) != "array" then
                    error("Each device must have a Parameter array")
                else
                    .Parameter | length
                end
            ] | add // 0
        end
    ' "$parameters")"; then
        bashio::log.error "Cannot read parameter file $parameters. Check the JSON syntax and the Devices and Parameter arrays."
        return 1
    fi

    bashio::log.info "Parameter file $parameters contains $parameter_count parameters."
    if (( parameter_count > 150 )); then
        echo
        echo "======= WARNING WARNING WARNING ======="
        echo "Your parameter file $parameters contains $parameter_count parameters!"
        echo "If you encounter issues with disconnects or some parameters not being updated, read here:"
        echo "https://github.com/aschwarz1984/my-ism7-addon?tab=readme-ov-file#important-if-some-entities-are-unavailable"
        echo "======= WARNING WARNING WARNING ======="
        echo
    fi
}

function start_ism7mqtt() {
    export ISM7_HOMEASSISTANT_ID=$1
    export ISM7_IP=$2
    export ISM7_PASSWORD=$3
    export ISM7_INTERVAL=$4

    echo "Removing legacy retained topics for $ISM7_HOMEASSISTANT_ID ..." | ts
    mosquitto_sub -h "$ISM7_MQTTHOST" -p "$ISM7_MQTTPORT" --username "$ISM7_MQTTUSERNAME" --pw "$ISM7_MQTTPASSWORD" --retained-only -t 'homeassistant/#'  -W 1 -v 2>/dev/null | grep "/${ISM7_HOMEASSISTANT_ID}_.* {" | cut -f1 -d' {' | while read line; do
      echo "Removing $line" | ts
      mosquitto_pub -h "$ISM7_MQTTHOST" -p "$ISM7_MQTTPORT" --username "$ISM7_MQTTUSERNAME" --pw "$ISM7_MQTTPASSWORD" -t "${line}" -r -n
    done || true

    if [ -z "$ISM7_INTERVAL" ]; then
        export ISM7_INTERVAL=60
    fi

    cd /app

    parameters="/config/ism7-parameters-$ISM7_HOMEASSISTANT_ID.json"
    if ! [ -f $parameters ]; then
        echo "Creating initial configuration $parameters"
        /app/ism7config -t $parameters | ts
        if ! [ -f $parameters ]; then
            echo "Parameter file creation seems to have failed. Please check the add-on log and your ISM7 connection."
            exit 1
        fi
    fi

    check_parameter_file "$parameters"

    # Not really needed, most of it could also be read from env, but helps identifying which process is which
    ISM_ARGS="--hass-id=$ISM7_HOMEASSISTANT_ID --interval=$ISM7_INTERVAL --ipAddress=$ISM7_IP -t $parameters"
    if [[ "$DEBUG_LOGGING" == "true" ]]; then
        ISM_ARGS+=" -d"
    fi

    run_bridge_with_retries /app/ism7mqtt $ISM_ARGS
}

HA_DISCOVERY_ID=$(bashio::config 'device_name')
ISM7_IP=$(bashio::config 'ism7_ip')
ISM7_PASSWORD=$(bashio::config 'ism7_password')
INTERVAL=$(bashio::config 'interval')

lang=$(bashio::config 'language')
export ISM7_LANGUAGE="$(cut -d':' -f1 <<<"$lang")"

echo "Setting up ism7mqtt $HA_DISCOVERY_ID $ISM7_IP"
start_ism7mqtt $HA_DISCOVERY_ID $ISM7_IP $ISM7_PASSWORD $INTERVAL &


# Set username and password for the broker
for device in $(bashio::config 'additional_devices|keys'); do
  devname=$(bashio::config "additional_devices[${device}].device_name")
  ip=$(bashio::config "additional_devices[${device}].ism7_ip")
  password=$(bashio::config "additional_devices[${device}].ism7_password")
  interval=$(bashio::config "additional_devices[${device}].interval")

  echo "Setting up ism7mqtt $devname $ip"
  start_ism7mqtt $devname $ip $password $interval &
done


wait
