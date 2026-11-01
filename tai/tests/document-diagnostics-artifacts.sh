#!/usr/bin/env bash
set -euo pipefail
dotnet run --project tai/tests/document-diagnostics-offline/document-diagnostics-offline.csproj -c Release --no-build -- \
  'C:/Users/xu peng kun/AppData/Local/TAI/artifacts/DB_TrafficCfg.s7dcl' \
  'C:/Users/xu peng kun/AppData/Local/TAI/artifacts/FC1_Control_v4.s7dcl' \
  'C:/Users/xu peng kun/AppData/Local/TAI/artifacts/cur_check3/块_1.s7dcl' \
  'C:/Users/xu peng kun/AppData/Local/TAI/artifacts/backup_before_delete/程序块/DB_Alarm.s7dcl'
