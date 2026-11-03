#ifndef WAYPOINT_RDP_SHIM_H
#define WAYPOINT_RDP_SHIM_H

// Cienka warstwa nad FreeRDP 3 dla Waypointa: jedno połączenie = jeden wątek z pętlą zdarzeń FreeRDP,
// obraz pulpitu w buforze BGRA (kopia chroniona blokadą), wejście z klawiatury i myszy, zmiana
// rozdzielczości przez kanał Display Control. Gdy biblioteka jest budowana bez FreeRDP
// (WP_HAVE_FREERDP niezdefiniowane), wp_rdp_start zwraca NULL, a wp_rdp_available() — 0.

#include <stdint.h>

typedef struct wp_rdp wp_rdp;

enum {
    WP_RDP_CONNECTING = 1,
    WP_RDP_CONNECTED = 2,
    WP_RDP_DISCONNECTED = 3,
};

/// Wynik pytania o certyfikat serwera.
enum { WP_CERT_REJECT = 0, WP_CERT_ACCEPT_ALWAYS = 1, WP_CERT_ACCEPT_ONCE = 2 };

typedef struct {
    void *user;
    /// Zmienił się fragment obrazu (x, y, w, h) — odczyt przez wp_rdp_lock_frame. Wątek połączenia.
    void (*frame)(void *user, int x, int y, int w, int h);
    /// Nowy rozmiar pulpitu (po połączeniu albo zmianie rozdzielczości). Wątek połączenia.
    void (*resized)(void *user, int width, int height);
    /// Zmiana stanu; przy WP_RDP_DISCONNECTED: kod błędu FreeRDP (0 = zwykłe rozłączenie) i opis.
    void (*state)(void *user, int state, uint32_t error, const char *message);
    /// Certyfikat serwera nieznany albo zmieniony (changed = 1, old_fingerprint wypełnione).
    /// Wywoływane w wątku połączenia — może blokować do odpowiedzi użytkownika.
    int (*verify_certificate)(void *user, const char *host, uint16_t port, const char *common_name,
                              const char *subject, const char *issuer, const char *fingerprint,
                              int changed, const char *old_fingerprint, int gateway);
    /// Brak hasła (albo odrzucone przy bramie) — wypełnij bufory i zwróć 1, albo 0 = przerwij.
    int (*authenticate)(void *user, char *username, int username_len, char *password, int password_len,
                        char *domain, int domain_len, int gateway);
    /// Tekst ze schowka sesji (UTF-8) — po skopiowaniu czegoś po stronie serwera. Wątek kanału.
    void (*clipboard_text)(void *user, const char *utf8);
    /// Pliki w schowku sesji (skopiowane w Eksploratorze): ścieżki względne z '/', rozmiar (-1 = nieznany),
    /// katalog. Treść pobiera się przez wp_rdp_clipboard_request_file. Wątek kanału.
    void (*clipboard_files)(void *user, int count, const char *const *names, const int64_t *sizes, const int *is_dir);
    /// Odpowiedź na wp_rdp_clipboard_request_file (ok = 0: błąd). Rozmiar: 8 bajtów little-endian.
    void (*clipboard_file_data)(void *user, uint32_t stream_id, int ok, const uint8_t *data, uint32_t length);
    /// Kursor z sesji: WP_CURSOR_IMAGE (obraz BGRA, nieprzemnożona alfa, w pikselach sesji, punkt
    /// aktywny hot_x/hot_y), WP_CURSOR_HIDDEN albo WP_CURSOR_DEFAULT. Wątek połączenia.
    void (*cursor)(void *user, int kind, int width, int height, int hot_x, int hot_y, const uint8_t *bgra);
    /// Serwer chce treści schowka ogłoszonego przez wp_rdp_announce_clipboard (wklejenie w Windows):
    /// odczytaj schowek Maca i podaj go przez wp_rdp_set_clipboard_text/files. Wątek kanału — wolno blokować.
    void (*clipboard_provide)(void *user);
} wp_rdp_callbacks;

enum { WP_CURSOR_IMAGE = 1, WP_CURSOR_HIDDEN = 0, WP_CURSOR_DEFAULT = 2 };

typedef struct {
    const char *host;
    uint16_t port;
    const char *username;
    const char *domain;
    const char *password;
    int width, height;          // piksele (fizyczne)
    int scale_percent;          // DesktopScaleFactor (100, 125, 150, 200…)
    int admin_session;          // /admin
    int redirect_clipboard;
    const char *config_dir;     // katalog na znane certyfikaty FreeRDP
    const char *client_hostname;
    // Brama RD (puste = brak)
    const char *gateway_host;
    uint16_t gateway_port;
    const char *gateway_username;
    const char *gateway_domain;
    const char *gateway_password;
    int gateway_same_credentials;
    int ignore_certificate;
    /// Folder Maca widoczny w sesji jako dysk (Ten komputer → „<nazwa> na <Mac>"); NULL = bez.
    const char *drive_path;
    const char *drive_name;
    /// Dziennik diagnostyczny (katalog; NULL = tylko błędy na stderr). Plik waypoint-rdp.log —
    /// przebieg połączenia i wymiany schowka (bez haseł i treści schowka). Ustawienie dla całego procesu.
    const char *log_dir;     // tylko testy
} wp_rdp_config;

int wp_rdp_available(void);
wp_rdp *wp_rdp_start(const wp_rdp_config *config, const wp_rdp_callbacks *callbacks);
/// Rozłącza (jeśli trzeba), czeka na koniec wątku i zwalnia wszystko. Po tym callbacki nie są wołane.
void wp_rdp_stop(wp_rdp *r);

/// Dostęp do obrazu: wskaźnik, rozmiar, stride (BGRA, 4 bajty na piksel). Zawsze wołać unlock.
int wp_rdp_lock_frame(wp_rdp *r, const uint8_t **data, int *width, int *height, int *stride);
void wp_rdp_unlock_frame(wp_rdp *r);

/// Klawisz po kodzie skanowania RDP (bit 0x100 = extended).
void wp_rdp_key(wp_rdp *r, int down, uint32_t scancode);
/// Znak Unicode (UTF-16) — dla znaków, których nie da się wysłać kodem klawisza.
void wp_rdp_unicode(wp_rdp *r, int down, uint16_t code);
/// Mysz: flagi PTR_FLAGS_* (przesunięcie, przyciski, kółko) w pikselach pulpitu zdalnego.
void wp_rdp_mouse(wp_rdp *r, uint16_t flags, int x, int y);
/// Dodatkowe przyciski myszy (PTR_XFLAGS_*).
void wp_rdp_mouse_extended(wp_rdp *r, uint16_t flags, int x, int y);
/// Prośba o zmianę rozdzielczości sesji (Display Control); bez kanału — ignorowana.
void wp_rdp_resize(wp_rdp *r, int width, int height, int scale_percent);
/// Synchronizacja stanu Caps/Num/Scroll Lock (bity KBD_SYNC_*).
void wp_rdp_sync_locks(wp_rdp *r, uint32_t flags);
/// Tekst ze schowka Maca dla sesji (UTF-8; NULL/pusty = brak). Serwer pobierze go przy wklejeniu.
void wp_rdp_set_clipboard_text(wp_rdp *r, const char *utf8);
/// Ogłasza serwerowi, że w schowku Maca jest tekst / są pliki, bez odczytu treści (ochrona schowka
/// w macOS: odczyt w tle może wymagać zgody). Treść — dopiero na prośbę serwera (clipboard_provide).
void wp_rdp_announce_clipboard(wp_rdp *r, int has_text, int has_files);
/// Wpis do dziennika diagnostycznego (jeśli włączony) — zdarzenia po stronie Maca.
void wp_rdp_log(const char *message);
/// Czy serwer otworzył kanał schowka (przysłał Monitor Ready). 0 = jeszcze nie / wyłączony na serwerze.
int wp_rdp_clipboard_ready(wp_rdp *r);
/// Pliki z Maca dla sesji (wklejenie w Eksploratorze): nazwy względne z '/' (katalogi przed zawartością),
/// pełne ścieżki, rozmiary, katalog = 1. Zastępuje tekst w schowku.
void wp_rdp_set_clipboard_files(wp_rdp *r, int count, const char *const *names, const char *const *paths,
                                const int64_t *sizes, const int *is_dir);
/// Prośba o rozmiar (size_only) albo fragment pliku `index` z listy sesji; jedna naraz. 0 = nie wysłano.
int wp_rdp_clipboard_request_file(wp_rdp *r, uint32_t stream_id, int index, int size_only,
                                  uint64_t position, uint32_t length);

/// Czy kod błędu FreeRDP oznacza odrzucone poświadczenia (złe hasło, konto zablokowane itp.).
int wp_rdp_error_is_auth(uint32_t error);

/// Kod klawisza macOS (NSEvent.keyCode) → kod skanowania RDP (0 = brak).
uint32_t wp_rdp_scancode_from_mac_keycode(uint32_t keycode);

#endif
