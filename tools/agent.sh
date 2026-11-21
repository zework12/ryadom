#!/bin/sh
# Управление вторым компьютером через «Рядом» (для Claude), без окна и без фокуса.
#   agent.sh status
#   agent.sh frame [scale] [файл.png]      — снимок того экрана
#   agent.sh click X Y [right] [double]
#   agent.sh move X Y | scroll X Y N
#   agent.sh key "ctrl+v" | text "строка"
#   agent.sh lock on|off                   — выключить/вернуть мышь и клавиатуру человека
#   agent.sh run "команда PowerShell" [папка] — фоновая задача там (живёт и после отключения)
#   agent.sh jobs | out ID [байт] | kill ID  — список задач, вывод, остановка
#   agent.sh ad <аргументы agent-desktop>   — agent-desktop на том компьютере, напр.: ad snapshot --skeleton -i --compact
KEYFILE="$LOCALAPPDATA/Ryadom/agent.json"
[ -f "$KEYFILE" ] || { echo "нет сеанса «Рядом» (нет $KEYFILE)"; exit 1; }
PORT=$(sed -n 's/.*"port":\([0-9]*\).*/\1/p' "$KEYFILE")
TOKEN=$(sed -n 's/.*"token":"\([0-9A-F]*\)".*/\1/p' "$KEYFILE")
U="http://127.0.0.1:$PORT"
H="X-Token: $TOKEN"
case "$1" in
  status) curl -s -H "$H" "$U/status"; echo ;;
  frame) OUT="${3:-$(dirname "$0")/../testrun/remote.png}"; curl -s -H "$H" -o "$OUT" "$U/frame?scale=${2:-0.6}" && echo "$OUT" ;;
  click) B=left; D=0; [ "$4" = right ] && B=right; [ "$4" = double ] || [ "$5" = double ] && D=1
         curl -s -X POST -H "$H" "$U/click?x=$2&y=$3&button=$B&double=$D"; echo ;;
  move) curl -s -X POST -H "$H" "$U/move?x=$2&y=$3"; echo ;;
  scroll) curl -s -X POST -H "$H" "$U/scroll?x=$2&y=$3&n=$4"; echo ;;
  key) C=$(printf "%s" "$2" | sed "s/+/%2B/g"); curl -s -X POST -H "$H" "$U/key?combo=$C"; echo ;;
  text) printf '%s' "$2" | curl -s -X POST -H "$H" -H "Content-Type: text/plain; charset=utf-8" --data-binary @- "$U/text"; echo ;;
  lock) [ "$2" = off ] && ON=0 || ON=1; curl -s -X POST -H "$H" "$U/lock?on=$ON"; echo ;;
  ad) shift; python -c "import json,sys; sys.stdout.write(json.dumps(sys.argv[1:]))" "$@" | curl -s -m 110 -X POST -H "$H" -H "Content-Type: application/json; charset=utf-8" --data-binary @- "$U/ad"; echo ;;
  run) python -c "import json,sys; sys.stdout.write(json.dumps({'op':'run','script':sys.argv[1],'dir':(sys.argv[2] if len(sys.argv)>2 else None)}))" "$2" ${3:+"$3"} | curl -s -m 110 -X POST -H "$H" -H "Content-Type: application/json; charset=utf-8" --data-binary @- "$U/job"; echo ;;
  jobs) printf '{"op":"list"}' | curl -s -m 110 -X POST -H "$H" --data-binary @- "$U/job"; echo ;;
  out) printf '{"op":"out","id":"%s","tail":%s}' "$2" "${3:-20000}" | curl -s -m 110 -X POST -H "$H" --data-binary @- "$U/job"; echo ;;
  kill) printf '{"op":"kill","id":"%s"}' "$2" | curl -s -m 110 -X POST -H "$H" --data-binary @- "$U/job"; echo ;;
  *) sed -n 2,13p "$0" ;;
esac
