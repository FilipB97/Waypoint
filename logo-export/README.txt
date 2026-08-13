WayPoint — logo / ikona (znak „W" — trasa z punktami)
=====================================================

Znak: łamana trasy w kształcie „W" z zaznaczonymi punktami początku i końca
(pierścienie) oraz punktem docelowym pośrodku (biała kropka).

Kolory marki
  Gradient znaku      #5B8CFF  ->  #7C4DFF   (niebieski -> fiolet, po przekątnej)
  Pierścienie punktów #7AA2FF
  Punkt docelowy      #FFFFFF
  Ink (tło kafla)     #0F1117
  Paper (tło jasne)   #EEF1F4

Wszystko poniżej jest generowane z jednego mastera — waypoint-logo.svg.

MASTER
  waypoint-logo.svg                  kafel 512x512 (r=112) — źródło prawdy dla całej reszty

IKONA APLIKACJI — kafel (ciemne tło + znak); tak wygląda ikona w Windows
  waypoint-icon-tile.svg             wektor
  waypoint.ico                       wielorozmiarowy .ico (16-256)
  icon/waypoint-*.png                16/32/48/64/128/256/512 px

  Kafel jest wariantem DOMYŚLNYM także na pasku zadań: niesie własne ciemne tło,
  więc czyta się tak samo na jasnym i ciemnym pasku (stąd nie ma już osobnego
  wariantu „bright", który był potrzebny przy jednolitym, ciemnym znaku kobaltowym).

ZNAK BEZ TŁA — przezroczysty, do nakładek i lockupów
  waypoint-glyph.svg                 wektor
  waypoint-glyph.ico                 wielorozmiarowy .ico (16-256)
  glyph/waypoint-glyph-*.png         16/32/48/64/128/256/512 px

ZNAK NA TŁO (do dokumentów)
  waypoint-mark-on-light.svg / -512.png / -256.png    (na jasne tło — oczka punktów w kolorze Paper)
  waypoint-mark-on-dark.svg  / -512.png / -256.png    (na ciemne tło — oczka punktów w kolorze Ink)

Podmiana ikony w aplikacji WPF:
  scripts\waypoint-icon.png  ->  scripts\make-icon.ps1  ->  src\RdpManager\Assets\app.ico
  (w .csproj: <ApplicationIcon>Assets\app.ico</ApplicationIcon>)
