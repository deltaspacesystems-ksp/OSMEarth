#!/usr/bin/env bash
# Buduje paczki OSMRoads dla CALEGO SWIATA z ekstraktow Geofabrika.
#
#   ./build-world.sh                 - wszystko (523 regiony)
#   ./build-world.sh europe          - tylko regiony spod europe/
#   ./build-world.sh "" 4            - wszystko, ale pomin ekstrakty > 4 GB
#
# Dlaczego nie z planet.osm.pbf: konwerter trzyma wspolrzedne wezlow w RAM.
# Caly swiat naraz to rzad 80-120 GB pamieci - nie do zrobienia na 32 GB.
# A skan 88 GB kosztuje ~3 h NIEZALEZNIE od tego, jak maly wycinek chcesz,
# wiec region po regionie jest i wykonalny, i szybszy.
#
# Wznawialny: region, ktory ma juz paczke, jest pomijany. Mozna przerwac
# w dowolnym momencie i uruchomic ponownie.
set -u

FILTER="${1:-}"
MAXGB="${2:-8}"
OUT="${3:-/d/OSMRoads/packs}"
WORK="${4:-/x/osm/work}"
LIST="${5:-}"

SCRATCH="${LOCALAPPDATA}/Temp/claude/D--/31f776c6-cbff-411d-9996-4e2044dfea98/scratchpad"
[ -n "$LIST" ] || LIST="$SCRATCH/leaves.txt"

[ -f "$LIST" ] || { echo "brak listy regionow: $LIST"; exit 1; }
mkdir -p "$OUT" "$WORK"

LOG="$OUT/build-world.log"
echo "=== start $(date '+%Y-%m-%d %H:%M:%S') filtr='$FILTER' max=${MAXGB}GB ===" >> "$LOG"

total=0; done_n=0; skip=0; fail=0
startAll=$(date +%s)

while IFS=$'\t' read -r name url; do
  [ -n "$name" ] || continue
  [ -z "$FILTER" ] || case "$url" in *"/$FILTER/"*) ;; *) continue ;; esac
  total=$((total+1))

  # Juz zrobione - pomijamy. To czyni caly skrypt wznawialnym.
  if [ -f "$OUT/$name.bin" ] && [ -f "$OUT/$name.idx" ]; then
    skip=$((skip+1)); continue
  fi

  sz=$(curl -sIL --max-time 25 "$url" | grep -i "^content-length" | tail -1 | tr -d '\r' | awk '{print $2}')
  [ -n "$sz" ] || { echo "  $name: brak rozmiaru, pomijam" | tee -a "$LOG"; fail=$((fail+1)); continue; }
  gb=$(awk "BEGIN{printf \"%.2f\", $sz/1073741824}")

  # Za duzy ekstrakt nie zmiesci sie w pamieci - jego podregiony i tak
  # sa na liscie osobno, wiec pominiecie nie robi dziury w pokryciu.
  if awk "BEGIN{exit !($gb > $MAXGB)}"; then
    echo "  $name: $gb GB > ${MAXGB} GB, pomijam" | tee -a "$LOG"
    skip=$((skip+1)); continue
  fi

  pbf="$WORK/$name.osm.pbf"
  t0=$(date +%s)
  printf "[%3d] %-26s %5s GB  pobieram ... " "$total" "$name" "$gb"

  if ! curl -sL --retry 2 --max-time 3600 -o "$pbf" "$url"; then
    echo "BLAD pobierania" | tee -a "$LOG"; rm -f "$pbf"; fail=$((fail+1)); continue
  fi

  printf "mieli ... "
  if dotnet run -c Release --project /d/OSMRoads/tools/OsmPack/OsmPack.csproj -- \
       "$pbf" "$OUT" 0 "$name" > "$WORK/$name.log" 2>&1; then
    rm -f "$pbf"
    out=$(stat -c %s "$OUT/$name.bin" 2>/dev/null || echo 0)
    t=$(( $(date +%s) - t0 ))
    printf "OK  %.0f MB  %ds\n" "$(awk "BEGIN{print $out/1048576}")" "$t"
    echo "$name OK $(awk "BEGIN{printf \"%.1f\", $out/1048576}")MB ${t}s" >> "$LOG"
    done_n=$((done_n+1))
  else
    echo "BLAD konwersji (log: $WORK/$name.log)" | tee -a "$LOG"
    rm -f "$pbf"; fail=$((fail+1))
  fi
done < "$LIST"

mins=$(( ($(date +%s) - startAll) / 60 ))
size=$(du -sm "$OUT" 2>/dev/null | cut -f1)
echo
echo "=== KONIEC: $done_n zrobionych, $skip pominietych, $fail bledow, ${mins} min ==="
echo "paczki zajmuja: ${size} MB"
echo "=== koniec $(date '+%Y-%m-%d %H:%M:%S'): $done_n ok, $skip skip, $fail err, ${mins}min, ${size}MB ===" >> "$LOG"
