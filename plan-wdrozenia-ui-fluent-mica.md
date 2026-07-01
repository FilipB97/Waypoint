# Plan wdrożenia — RDP Manager (Fluent / Mica)

Wybrany kierunek wizualny: **Opcja 2 — Windows 11 Fluent / Mica**, z dodatkową funkcją dopracowaną w tej iteracji: przełącznik na inne połączenia bezpośrednio z paska w trybie pełnoekranowym.

📎 **Mockup:** `mockup-2-fluent-mica.html` (zaktualizowana wersja — patrz punkt 5)
📎 Ten dokument rozszerza wcześniejszy `plan-rdp-manager.md` (Fazy 0–8, stack technologiczny) o konkretne kroki wdrożenia interfejsu zgodnie z zatwierdzonym mockupem.

---

## 1. Co dokładnie wdrażamy

- Layout: custom titlebar → ikon-rail (NavigationView) → drzewo serwerów → zakładki sesji → canvas RDP
- Tryb pełnoekranowy uruchamiany z toolbara sesji (jak w mstsc), ale z auto-chowającym się paskiem
- **Nowość:** pasek w trybie pełnoekranowym ma przycisk „inne połączenia”, który rozwija listę (otwarte sesje + wszystkie serwery) i pozwala przełączyć się bez wychodzenia z pełnego ekranu

---

## 2. Mapowanie mockupu na komponenty WPF

| Element mockupu | Komponent WPF |
|---|---|
| Titlebar (─ ▢ ✕) | `WindowChrome` + custom `TitleBar` UserControl |
| Ikon-rail po lewej | `NavigationRailControl` (UserControl, statyczne ikony) |
| Drzewo serwerów + szukajka | `ServerTreeView` UserControl, `TreeView`/`ItemsControl` grupowany po `Group` |
| Zakładki sesji | Custom `TabControl` (styl WPF-UI, wygląd Windows Terminal) |
| Canvas RDP | `WindowsFormsHost` + `AxMSTSCLib.AxMsRdpClient` per zakładka |
| Pływający pasek pełnoekranowy | `Popup` lub `Border` z `TranslateTransform` + `Storyboard` |
| Flyout „inne połączenia” | `Popup` (`StaysOpen="False"`) z listą filtrowaną |

---

## 3. Tryb pełnoekranowy — konkretna implementacja

**Poziom okna:**
Najprościej: w ramach tego samego `Window` — ukryj `Visibility=Collapsed` dla rail/sidebar/tabstrip/toolbar, ustaw `WindowStyle="None"` i `WindowState="Maximized"`. To odpowiada dokładnie temu, co pokazuje mockup, i wystarcza na MVP.

Prawdziwe wieloekranowe fullscreen „jak w mstsc” (osobne okno per monitor) to osobny temat z Fazy 5 (multi-monitor) — nie blokuje tego etapu, warto go potraktować jako rozszerzenie później.

**Pływający pasek (auto-hide):**
- Niewidoczny „hot zone” — wąski `Grid` (np. 6px) przyklejony do góry canvas RDP, nasłuchujący `MouseEnter`
- Pasek jako `Border` z `RenderTransform="TranslateTransform"`, domyślnie przesunięty w górę poza kadr
- `MouseEnter` na hot zone → `Storyboard` animujący `TranslateTransform.Y` do 0 (pokazanie); `MouseLeave` na całym pasku → animacja z powrotem

**Flyout „inne połączenia”:**
- `Popup` zaczepiony pod paskiem, `PlacementTarget` = przycisk „inne połączenia”, `StaysOpen="False"` (zamyka się automatycznie po kliknięciu poza nim — dokładnie tak jak w mockupie)
- W środku: `TextBox` do filtrowania + dwie sekcje (`ItemsControl`):
  - **Otwarte sesje** — bindowane do kolekcji aktualnie otwartych zakładek
  - **Wszystkie serwery** — bindowane do pełnej listy z repozytorium (Faza 2 wcześniejszego planu)
- Kliknięcie pozycji:
  - jeśli to już otwarta sesja → tylko `SelectedIndex` na `TabControl` (natychmiastowe przełączenie, bez przeładowania)
  - jeśli to serwer bez otwartej sesji → wywołanie tej samej komendy „połącz”, co z sidebaru, i przełączenie po nawiązaniu połączenia
- Cały czas w trybie pełnoekranowym — okno się nie zwija

---

## 4. Model danych potrzebny pod ten mechanizm

Żeby pasek w trybie pełnoekranowym i zwykły `TabControl` zawsze pokazywały ten sam, spójny stan:

- Jeden współdzielony `SessionsViewModel`, widoczny zarówno z zakładek, jak i z flyoutu
- `ObservableCollection<SessionTabViewModel>` — otwarte sesje (nazwa, status, host)
- `ObservableCollection<ServerViewModel>` — pełna lista serwerów z Fazy 2 (SQLite)
- Zmiana aktywnej sesji w jednym miejscu automatycznie odzwierciedla się w drugim (standardowy binding, bez dodatkowej synchronizacji ręcznej)

---

## 5. Co zmieniło się w mockupie

Plik `mockup-2-fluent-mica.html` został zaktualizowany względem poprzedniej wersji:

- Pasek pełnoekranowy ma teraz przycisk **„inne połączenia”**
- Kliknięcie rozwija listę z podziałem na **Otwarte sesje** i **Wszystkie serwery** (z polem szukania)
- Kliknięcie pozycji na liście przełącza aktywne połączenie i aktualizuje nazwę na pasku — bez opuszczania trybu pełnoekranowego
- Lista zamyka się automatycznie po kliknięciu poza nią

---

## 6. Kolejność prac

1. Custom `WindowChrome` + titlebar (bez rail jeszcze)
2. Statyczny `NavigationRailControl`
3. `ServerTreeView` na sztywnych danych testowych
4. Pierwsza działająca sesja RDP w zakładce (`WindowsFormsHost` + `AxMSTSCLib`) — pokrywa się z Fazą 1 wcześniejszego planu
5. Podstawowy tryb pełnoekranowy (ukrycie chrome + `WindowState="Maximized"`)
6. Pływający pasek z auto-hide (`Popup`/`Border` + `Storyboard`)
7. Flyout „inne połączenia” podpięty pod `SessionsViewModel`
8. Dopiero teraz pełne zarządzanie serwerami (CRUD, SQLite) z Faz 2 i 4 wcześniejszego planu

---

## 7. Ryzyka do sprawdzenia zawczasu

- `WindowsFormsHost` + `AxMSTSCLib` bywa niestabilny przy zmianie `WindowStyle`/`WindowState` „w locie” (znane przypadki migotania/reinicjalizacji kontrolki ActiveX) — zrób szybki test w Fazie 1, zanim zainwestujesz czas w resztę UI pełnoekranowego
- Prawdziwy efekt Mica (nie symulacja z mockupu) wymaga Windows 11 + `WindowBackdropType.Mica` z WPF-UI — sprawdź wcześniej, czy współpracuje poprawnie z custom `WindowChrome` (te dwa mechanizmy czasem się gryzą)

---

**Następny krok:** Faza 1 z wcześniejszego planu (szkielet WPF + jedno połączenie RDP) to nadal najlepszy punkt startu — potwierdza fundament (kontrolka RDP działa), zanim zbudujesz na nim cały ten interfejs.
