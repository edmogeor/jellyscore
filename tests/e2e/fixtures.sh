#!/usr/bin/env bash
set -euo pipefail
docker compose -f tests/e2e/compose.yaml exec -T jellyfin sh <<'SH'
set -eu
movie() {
  folder="${4:-/media/movies}/$1 ($3)"
  mkdir -p "$folder"
  if [ ! -e "$folder/$1.mp4" ]; then
    /usr/lib/jellyfin-ffmpeg/ffmpeg -loglevel error -f lavfi -i color=c=black:s=320x240:r=1 -t 1 -c:v mpeg4 -y "$folder/$1.mp4"
  fi
  tmdb=''
  if [ -n "${5:-}" ]; then tmdb="<uniqueid type=\"tmdb\">$5</uniqueid>"; fi
  if [ ! -e "$folder/movie.nfo" ]; then
    printf '<movie><title>%s</title><originaltitle>%s</originaltitle><year>%s</year>%s<lockdata>true</lockdata></movie>\n' "$2" "$2" "$3" "$tmdb" > "$folder/movie.nfo"
  fi
}
movie "Harry Potter and the Sorcerer's Stone" 'Harry Potter and the Sorcerer&apos;s Stone' 2001
movie Dune Dune 2021
movie 'User Theme' 'User Theme' 2000
if [ ! -e '/media/movies/User Theme (2000)/theme.mp3' ]; then
  /usr/lib/jellyfin-ffmpeg/ffmpeg -loglevel error -f lavfi -i anullsrc=r=44100:cl=mono -t 2 -c:a libmp3lame -y '/media/movies/User Theme (2000)/theme.mp3'
fi
cp '/media/movies/User Theme (2000)/theme.mp3' /tmp/user-theme-original
movie 'Unselected Example' 'Unselected Example' 1999 /media/unused
series() {
  folder="/media/shows/$1 ($2)"
  mkdir -p "$folder/Season 01"
  if [ ! -e "$folder/Season 01/S01E01 Pilot.mp4" ]; then
    /usr/lib/jellyfin-ffmpeg/ffmpeg -loglevel error -f lavfi -i color=c=black:s=320x240:r=1 -t 1 -c:v mpeg4 -y "$folder/Season 01/S01E01 Pilot.mp4"
  fi
  if [ ! -e "$folder/tvshow.nfo" ]; then
    printf '<tvshow><title>%s</title><year>%s</year><uniqueid type="tvdb">71470</uniqueid><lockdata>true</lockdata></tvshow>\n' "$1" "$2" > "$folder/tvshow.nfo"
  fi
}
series 'Star Trek: The Next Generation' 1987
SH
