# Wizualizacje

Statyczne makiety interfejsu Waypointa. Otwiera się je bezpośrednio w przeglądarce —
nie wymagają serwera, budowania ani żadnych zależności poza czcionką z Google Fonts
(bez internetu strona nadal działa, tylko monospace spada na Consolas).

Folder celowo **nie** leży w `site/`, bo tamten katalog jest publikowany na GitHub Pages.
Te pliki są materiałem roboczym repozytorium, a nie treścią publiczną.

## `waypoint-atlas.html`

Interaktywna makieta całego okna aplikacji — wszystkie widoki i moduły w jednym pliku.

| element | co robi |
|---|---|
| szyna po lewej | przełącza widoki: Pulpit, Połączenia, Ostatnie, REST, Generator haseł, Ustawienia |
| karty u góry | przełączają sesje: pulpit zdalny, terminal SSH, panel plików; `×` odsłania stan pusty |
| Ustawienia → Interfejs | motyw, sześć presetów palety i pięć akcentów **przemalowują stronę na żywo** |
| ikona kadru na pasku kart | tryb skupienia — chowa pasek tytułu i panel boczny |
| panel plików | okruszki nawigują po drzewie, lejek filtruje, dwuklik na pliku otwiera podgląd |

### Skąd biorą się kolory

Wszystkie tokeny w bloku `:root` odpowiadają kluczom z
`src/RdpManager/Themes/Palette.Light.xaml` i `Palette.Dark.xaml`, a presety —
wpisom z `src/RdpManager/ThemePresets.cs`. Przy zmianie palety w aplikacji warto
przenieść ją tutaj, inaczej makieta zacznie kłamać.

Okruszki liczą cel jako prefiks ścieżki — tą samą metodą co
`FileTransferPanel.BreadcrumbSegments`, żeby ścieżki windowsowe (`C:/Apps`)
zachowywały się tak samo jak w aplikacji.

### Dane

Przykładowe, wzorowane na realnym układzie: grupy `ABSYSCO | RDP`, `FTP`,
`ABSYSCO | SSH`, katalogi `C:/Apps` i `/repository/internal/pl/OMPro`. Żadne
z nich nie są pobierane z konfiguracji użytkownika — plik jest w pełni statyczny
i nie czyta ani nie wysyła niczego.
