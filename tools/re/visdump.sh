#!/bin/sh
# visdump.sh <addr> [lines] -- one function from the visibility decompile dump
# (tools/re/out/dumps/vis_all.txt with vis_index.tsv), by address.
d="$(dirname "$0")/out/dumps"
a="$1"; n="${2:-400}"
line=$(awk -F'\t' -v a="$a" '$1==a {print $5}' "$d/vis_index.tsv")
[ -z "$line" ] && { echo "no $a in vis_index.tsv"; exit 1; }
sed -n "${line},$((line+n))p" "$d/vis_all.txt"
