#!/usr/bin/env bash
# Dodaje region do lokalnego zbioru moda.
#
#   ./add-region.sh europe/poland
#   ./add-region.sh north-america/us/florida
#   ./add-region.sh asia/kazakhstan
#
# Pobiera ekstrakt z Geofabrika, przepuszcza przez osmpack, kasuje .pbf.
# Szczyt zajetosci dysku to JEDEN ekstrakt, nie suma wszystkich.
#
# Dlaczego nie z planet.osm.pbf: skan 88 GB kosztuje ~3 h niezaleznie od tego,
# jak maly wycinek chcesz. Ekstrakt Polski to 1.9 GB i 6 minut. Przy dwustu
# regionach ta roznica to tygodnie kontra jedna noc.
set -u

REGION="${1:-}"
OUT="${2:-/d/OSMRoads/packs}"
WORK="${3:-/x/osm/work}"

if [ -z "$REGION" ]; then
  echo "uzycie: add-region.sh <sciezka-geofabrik> [katalog-paczek] [katalog-roboczy]"
  echo "przyklad: add-region.sh europe/poland"
  exit 1
fi

NAME=$(echo "$REGION" | tr '/' '-')
URL="https://download.geofabrik.de/${REGION}-latest.osm.pbf"
PBF="$WORK/${NAME}.osm.pbf"

mkdir -p "$OUT" "$WORK"

# Konwerter trzyma wspolrzedne wezlow w RAM. Przy 32 GB pamieci ekstrakty
# powyzej ~8 GB zaczynaja byc ryzykowne - lepiej wziac podregion.
SIZE=$(curl -sIL --max-time 30 "$URL" | grep -i "^content-length" | tail -1 | tr -d '\r' | awk '{print $2}')
if [ -z "$SIZE" ]; then
  echo "nie znalazlem $URL"
  echo "sprawdz nazwe na https://download.geofabrik.de/"
  exit 1
fi
GB=$(awk "BEGIN{printf \"%.2f\", $SIZE/1073741824}")
echo "region : $REGION"
echo "rozmiar: $GB GB"

if awk "BEGIN{exit !($GB > 8)}"; then
  echo
  echo "UWAGA: ekstrakt powyzej 8 GB moze nie zmiescic sie w pamieci."
  echo "Wez mniejszy podregion, np. europe/germany/bayern zamiast europe/germany."
  echo "Przerywam. Wymus przez: FORCE=1 $0 $REGION"
  [ "${FORCE:-0}" = "1" ] || exit 1
fi

echo "pobieram ..."
curl -L --retry 3 --max-time 7200 -o "$PBF" "$URL" || { echo "pobieranie nieudane"; exit 1; }

echo "konwertuje ..."
dotnet run -c Release --project /d/OSMRoads/tools/OsmPack/OsmPack.csproj -- \
    "$PBF" "$OUT" 0 "$NAME" 2>&1 | tail -8

# Zrodlo kasujemy od razu - o to chodzi, zeby szczyt zajetosci byl jeden plik.
rm -f "$PBF"

echo
if [ -f "$OUT/$NAME.bin" ]; then
  echo "GOTOWE: $NAME"
  ls -la "$OUT/$NAME".* | awk '{printf "  %-34s %8.1f MB\n", $NF, $5/1048576}'
else
  echo "BLAD: paczka nie powstala"
  exit 1
fi
