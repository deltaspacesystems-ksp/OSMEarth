#!/usr/bin/env bash
# Kopiowanie duzego pliku z wznawianiem.
#
# cp zaczyna od zera po kazdym przerwaniu, a przy 88 GB przez siec to bolesne.
# Tutaj sprawdzamy, ile juz przeszlo, cofamy sie do pelnego bloku (ogon moze byc
# uciety w polowie zapisu) i dopisujemy reszte przez dd.
set -u
SRC="$1"; DST="$2"; BS=$((8*1024*1024))

[ -f "$SRC" ] || { echo "brak zrodla: $SRC"; exit 1; }
mkdir -p "$(dirname "$DST")"

TOTAL=$(stat -c %s "$SRC")
HAVE=$(stat -c %s "$DST" 2>/dev/null || echo 0)
BLOCKS=$((HAVE / BS))                 # tylko pelne bloki uznajemy za pewne
OFF=$((BLOCKS * BS))

if [ "$OFF" -gt 0 ]; then
  truncate -s "$OFF" "$DST"           # obetnij ewentualny niepelny ogon
  echo "wznawiam od $(awk "BEGIN{printf \"%.2f\", $OFF/1073741824}") GB"
else
  echo "start od zera"
fi

dd if="$SRC" of="$DST" bs=$BS skip=$BLOCKS seek=$BLOCKS conv=notrunc status=none

NEW=$(stat -c %s "$DST")
if [ "$NEW" -eq "$TOTAL" ]; then
  echo "GOTOWE: $(awk "BEGIN{printf \"%.1f\", $NEW/1073741824}") GB, rozmiary zgodne"
else
  echo "NIEPELNE: $NEW z $TOTAL"; exit 1
fi
