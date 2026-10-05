# Karta marki strony Waypoint (`site/`)

Opis, według którego składamy wygląd landingu na GitHub Pages. Fakty biorą się
z aplikacji (WPF, Fluent / Mica, logo „W” z węzłami trasy) i z treści strony.
Zasady wykonania: bez etykiet wersalikami z rozstrzeleniem, poświat, tekstu
w gradiencie, animacji w pętli, myślników w treści i pudełek wokół wszystkiego.

## Charakter

Narzędzie dla administratorów i programistów: precyzyjne, spokojne, natywne dla
Windows. Strona ma wyglądać jak dobrze ułożone okno aplikacji, nie jak reklama
startupu.

## Głos

Krótko i technicznie, po angielsku i po polsku (słownik `I18N` w `index.html`;
tekst w znaczniku = wersja EN). Konkret zamiast obietnic, bez wykrzykników
i myślników: dwukropek, przecinek albo nowe zdanie.

## Kolor

- Tło: `#080b14`, jedna jaśniejsza powierzchnia `--bg2` (pas bezpieczeństwa,
  blok pobierania). Bez poświat i rozmyć w tle.
- Akcent: jeden fiolet `#6c6dff` (przycisk główny, aktywna zakładka), jaśniejszy
  `#8a8bff` na tekst i węzły. Bez gradientów na przyciskach i w tekście.
- Kolory protokołów (RDP, SSH, SFTP, REST) tylko w makietach okna, jak w aplikacji.

## Krój

Segoe UI Variable (system Windows) do tekstu i nagłówków 700, Cascadia Code /
Consolas do etykiet sekcji, liczb i makiet terminala.

## Kształt

Kreski zamiast ramek: FAQ jako lista z liniami, szybki start jako przystanki na
jednej linii. Ramkę dostaje to, co jest osobnym przedmiotem: okno aplikacji,
makiety, tabela porównania, pas bezpieczeństwa, blok pobierania.

## Gest marki

Punkt trasy z logo: pusty węzeł na krótkim odcinku linii przy każdej etykiecie
sekcji (`.eye::before`), numer kroku w szybkim starcie jako węzeł na linii,
roadmapa jako węzły na jednej pionowej trasie.

## Energia i ruch

Energia 2 z 5, ruch 1 z 5. Okno w hero wstaje raz przy wejściu, sekcje wchodzą
raz przy przewijaniu, polecenie w terminalu wpisuje się raz. Kursor nie mruga
w pętli. Przy `prefers-reduced-motion` wszystko stoi.
