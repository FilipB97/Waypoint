WayPoint — logo / ikona (wariant 1a, kolor 2c Kobalt)
=====================================================

Kolory marki
  Kobalt (brand)      #2657D6
  Kobalt ekranowy     #3A6BE8   (jaśniejszy — lepiej widoczny na ciemnym pasku zadań)
  Ink (tło ciemne)    #0E1116
  Paper (tło jasne)   #EEF1F4

IKONA APLIKACJI — sama ikona, bez kwadratowego tła (przezroczyste PNG, dziurka w pinezce)
  waypoint-glyph.svg                 wektor, kolor brandowy #2657D6
  waypoint-glyph.ico                 wielorozmiarowy .ico (16–256) — dla aplikacji WPF
  glyph/waypoint-glyph-*.png         16/32/48/64/128/256/512 px

  waypoint-glyph-bright.svg          wariant #3A6BE8 (REKOMENDOWANY na pasek zadań)
  waypoint-glyph-bright.ico          wielorozmiarowy .ico
  glyph-bright/waypoint-glyph-*.png  16/32/48/64/128/256/512 px

WERSJA „TILE" — ikona w zaokrąglonym kwadracie (gdyby jednak była potrzebna)
  waypoint-icon-tile.svg
  waypoint.ico
  icon/waypoint-*.png                16–512 px

ZNAK NA TŁO (do lockupu / dokumentów)
  waypoint-mark-on-light.svg / -512.png / -256.png    (na jasne tło, kolor #2657D6)
  waypoint-mark-on-dark.svg  / -512.png / -256.png    (na ciemne tło, kolor #3A6BE8)

Podmiana ikony w aplikacji WPF:
  W .csproj:  <ApplicationIcon>waypoint-glyph-bright.ico</ApplicationIcon>
