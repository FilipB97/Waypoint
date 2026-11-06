# Eksport haseł (Windows) → import haseł (macOS)

## Cel
Przeniesienie zapisanych haseł połączeń z Waypoint dla Windows na Maca. Serwery i profile poświadczeń
przenosi się już dziś („Eksportuj profil…” + `credprofiles.json`). Hasła zostawały w Windows Credential
Manager. Plik z hasłami jest zaszyfrowany hasłem, które wymyśla użytkownik.

## Format pliku (wersja 1)
JSON w UTF-8, rozszerzenie `.json`, domyślna nazwa `waypoint-hasla-RRRR-MM-DD.json`:

```json
{
  "Format": "waypoint-passwords",
  "Version": 1,
  "Kdf": "PBKDF2-SHA256",
  "Iterations": 600000,
  "Salt": "<base64, 16 bajtów>",
  "Nonce": "<base64, 12 bajtów>",
  "Data": "<base64: szyfrogram || tag 16 bajtów>"
}
```

- Klucz: PBKDF2-HMAC-SHA256(hasło w UTF-8, Salt, Iterations) → 32 bajty.
- Szyfr: AES-256-GCM, bez AAD, tag 16 bajtów doklejony na końcu `Data`.
- Odszyfrowana treść: JSON w UTF-8 `{"Passwords": {"<konto>": "<hasło>", ...}}`.
- Konto = cel w Credential Manager bez przedrostka `RdpManager:`, czyli to samo, co konto w Pęku kluczy na Macu:
  - hasło serwera: `<Id serwera>`,
  - hasło profilu poświadczeń: `profile:<Id profilu>`.
- Odczyt akceptuje `Iterations` od 1 do 10 000 000. Spoza zakresu, inny `Format`, inna `Version`
  albo `Kdf` → plik nierozpoznany.

## Windows: Ustawienia → „Eksportuj hasła…”
- Zbiera hasła: serwery z `SavePassword` i niepustym hasłem pod `CredTarget` oraz profile
  poświadczeń z niepustym hasłem. Puste hasła pomija.
- Brak haseł → komunikat, plik nie powstaje.
- Pyta dwa razy o hasło do pliku (maskowane). Musi mieć co najmniej 8 znaków i oba wpisy muszą
  być takie same, inaczej komunikat i koniec.
- Zapis, w pasku statusu „Wyeksportowano haseł: N”.

## macOS: menu „Importuj hasła z Waypoint dla Windows…”
- Wybór pliku → pytanie o hasło (maskowane) → odszyfrowanie.
- Plik nierozpoznany → „To nie jest plik z hasłami Waypoint.”
- Złe hasło albo uszkodzony plik (GCM tego nie rozróżnia) → „Złe hasło albo uszkodzony plik.”
- Zapis do Pęku kluczy tylko dla kont, które mają odpowiednik na Macu: `<id>` istniejącego serwera,
  `profile:<id>` istniejącego profilu. Pozostałe są pomijane i liczone. Puste hasło z pliku nie
  kasuje istniejącego.
- Wynik: „Zaimportowano haseł: N.” Gdy coś pominięto, dochodzi: „Pominięto: M (brak takich połączeń
  na Macu: najpierw zaimportuj profil i profile poświadczeń).”

## Poza zakresem
Sekrety REST, import na Windows, eksport z Maca.

## Kryteria akceptacji
1. Plik zaszyfrowany przez wersję Windows odszyfrowuje się na Macu tym samym hasłem.
2. Wektor testowy wygenerowany niezależną implementacją (Node `crypto`) odszyfrowuje się w obu wersjach.
3. Złe hasło, zmieniony bajt w `Data`, obcy `Format` i `Iterations` spoza zakresu dają czytelny błąd, bez awarii.
4. Import zapisuje tylko konta istniejących serwerów i profili, liczy pominięte i nie kasuje haseł pustymi wartościami.
5. Eksport pomija serwery bez `SavePassword` i puste hasła, a brak haseł nie tworzy pliku.
