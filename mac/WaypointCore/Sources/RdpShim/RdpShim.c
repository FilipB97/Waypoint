#include "RdpShim.h"

#ifndef WP_HAVE_FREERDP

// Budowa bez FreeRDP (np. testy logiki na Linuksie bez bibliotek RDP) — tylko zaślepki.
int wp_rdp_available(void) { return 0; }
wp_rdp *wp_rdp_start(const wp_rdp_config *c, const wp_rdp_callbacks *cb) { (void)c; (void)cb; return 0; }
void wp_rdp_stop(wp_rdp *r) { (void)r; }
int wp_rdp_lock_frame(wp_rdp *r, const uint8_t **d, int *w, int *h, int *s) { (void)r; (void)d; (void)w; (void)h; (void)s; return 0; }
void wp_rdp_unlock_frame(wp_rdp *r) { (void)r; }
void wp_rdp_key(wp_rdp *r, int d, uint32_t s) { (void)r; (void)d; (void)s; }
void wp_rdp_unicode(wp_rdp *r, int d, uint16_t c) { (void)r; (void)d; (void)c; }
void wp_rdp_mouse(wp_rdp *r, uint16_t f, int x, int y) { (void)r; (void)f; (void)x; (void)y; }
void wp_rdp_mouse_extended(wp_rdp *r, uint16_t f, int x, int y) { (void)r; (void)f; (void)x; (void)y; }
void wp_rdp_resize(wp_rdp *r, int w, int h, int s) { (void)r; (void)w; (void)h; (void)s; }
void wp_rdp_sync_locks(wp_rdp *r, uint32_t f) { (void)r; (void)f; }
uint32_t wp_rdp_scancode_from_mac_keycode(uint32_t k) { (void)k; return 0; }
int wp_rdp_error_is_auth(uint32_t e) { (void)e; return 0; }

#else

#include <pthread.h>
#include <stdlib.h>
#include <string.h>

#include <freerdp/freerdp.h>
#include <freerdp/client.h>
#include <freerdp/client/channels.h>
#include <freerdp/client/disp.h>
#include <freerdp/channels/disp.h>
#include <freerdp/gdi/gdi.h>
#include <freerdp/codecs.h>
#include <freerdp/input.h>
#include <freerdp/error.h>
#include <freerdp/log.h>
#include <winpr/synch.h>
#include <winpr/input.h>

typedef struct {
    rdpClientContext common;   // musi być pierwsze — wspólna obsługa kanałów (RDPGFX itd.) rzutuje na nie
    wp_rdp *owner;
} wpContext;

struct wp_rdp {
    rdpContext *context;
    wp_rdp_callbacks cb;
    pthread_t thread;
    int thread_started;

    pthread_mutex_t frame_lock;
    uint8_t *frame;            // kopia obrazu dla interfejsu (wątek UI czyta pod blokadą)
    int width, height, stride;

    pthread_mutex_t disp_lock;
    DispClientContext *disp;
    int pending_w, pending_h, pending_scale;   // prośba o zmianę rozdzielczości (wysyła wątek połączenia)

    volatile int stopping;
};

static wp_rdp *owner_of(rdpContext *ctx) { return ctx ? ((wpContext *)ctx)->owner : NULL; }

static void notify_state(wp_rdp *r, int state, uint32_t error, const char *message) {
    if (r && r->cb.state && !r->stopping) r->cb.state(r->cb.user, state, error, message ? message : "");
}

// --- Obraz ---

/// Kopia obrazu po zmianie rozmiaru bufora GDI (pierwsze połączenie, DesktopResize, ResetGraphics).
static void sync_size(wp_rdp *r, rdpGdi *gdi) {
    if ((int)gdi->width == r->width && (int)gdi->height == r->height && r->frame) return;
    pthread_mutex_lock(&r->frame_lock);
    free(r->frame);
    r->width = (int)gdi->width;
    r->height = (int)gdi->height;
    r->stride = r->width * 4;
    r->frame = calloc((size_t)r->stride, (size_t)(r->height > 0 ? r->height : 1));
    pthread_mutex_unlock(&r->frame_lock);
    if (r->cb.resized && !r->stopping) r->cb.resized(r->cb.user, r->width, r->height);
}

static void copy_rect(wp_rdp *r, rdpGdi *gdi, int x, int y, int w, int h) {
    if (x < 0) { w += x; x = 0; }
    if (y < 0) { h += y; y = 0; }
    if (x + w > r->width) w = r->width - x;
    if (y + h > r->height) h = r->height - y;
    if (w <= 0 || h <= 0 || !r->frame) return;
    pthread_mutex_lock(&r->frame_lock);
    for (int row = y; row < y + h; row++)
        memcpy(r->frame + (size_t)row * r->stride + (size_t)x * 4,
               gdi->primary_buffer + (size_t)row * gdi->stride + (size_t)x * 4, (size_t)w * 4);
    pthread_mutex_unlock(&r->frame_lock);
    if (r->cb.frame && !r->stopping) r->cb.frame(r->cb.user, x, y, w, h);
}

static BOOL wp_begin_paint(rdpContext *context) {
    rdpGdi *gdi = context->gdi;
    if (gdi && gdi->primary && gdi->primary->hdc && gdi->primary->hdc->hwnd) {
        HGDI_WND hwnd = gdi->primary->hdc->hwnd;
        hwnd->invalid->null = TRUE;
        hwnd->ninvalid = 0;
    }
    return TRUE;
}

static BOOL wp_end_paint(rdpContext *context) {
    wp_rdp *r = owner_of(context);
    rdpGdi *gdi = context->gdi;
    if (!r || !gdi || !gdi->primary || !gdi->primary->hdc || !gdi->primary->hdc->hwnd) return TRUE;
    sync_size(r, gdi);
    HGDI_WND hwnd = gdi->primary->hdc->hwnd;
    if (hwnd->invalid->null) return TRUE;
    copy_rect(r, gdi, hwnd->invalid->x, hwnd->invalid->y, hwnd->invalid->w, hwnd->invalid->h);
    hwnd->invalid->null = TRUE;
    hwnd->ninvalid = 0;
    return TRUE;
}

static BOOL wp_desktop_resize(rdpContext *context) {
    wp_rdp *r = owner_of(context);
    rdpSettings *s = context->settings;
    UINT32 w = freerdp_settings_get_uint32(s, FreeRDP_DesktopWidth);
    UINT32 h = freerdp_settings_get_uint32(s, FreeRDP_DesktopHeight);
    if (!gdi_resize(context->gdi, w, h)) return FALSE;
    // Dekodery (np. planar) mają limit rozmiaru z chwili połączenia — bez resetu pierwsza większa
    // klatka po zmianie rozdzielczości zrywała sesję („planar->maxWidth < nSrcWidth").
    if (context->codecs && !freerdp_client_codecs_reset(context->codecs, FREERDP_CODEC_ALL, w, h)) return FALSE;
    if (r) {
        sync_size(r, context->gdi);
        copy_rect(r, context->gdi, 0, 0, r->width, r->height);
    }
    return TRUE;
}

static BOOL wp_keyboard_indicators(rdpContext *context, UINT16 flags) { (void)context; (void)flags; return TRUE; }
static BOOL wp_ime_status(rdpContext *c, UINT16 a, UINT32 b, UINT32 d) { (void)c; (void)a; (void)b; (void)d; return TRUE; }
static BOOL wp_play_sound(rdpContext *c, const PLAY_SOUND_UPDATE *p) { (void)c; (void)p; return TRUE; }

// --- Kanały ---

static void on_channel_connected(void *context, const ChannelConnectedEventArgs *e) {
    wpContext *wc = (wpContext *)context;
    if (strcmp(e->name, DISP_DVC_CHANNEL_NAME) == 0) {
        pthread_mutex_lock(&wc->owner->disp_lock);
        wc->owner->disp = (DispClientContext *)e->pInterface;
        pthread_mutex_unlock(&wc->owner->disp_lock);
    } else {
        freerdp_client_OnChannelConnectedEventHandler(&wc->common, e);
    }
}

static void on_channel_disconnected(void *context, const ChannelDisconnectedEventArgs *e) {
    wpContext *wc = (wpContext *)context;
    if (strcmp(e->name, DISP_DVC_CHANNEL_NAME) == 0) {
        pthread_mutex_lock(&wc->owner->disp_lock);
        wc->owner->disp = NULL;
        pthread_mutex_unlock(&wc->owner->disp_lock);
    } else {
        freerdp_client_OnChannelDisconnectedEventHandler(&wc->common, e);
    }
}

static void send_pending_resize(wp_rdp *r) {
    pthread_mutex_lock(&r->disp_lock);
    DispClientContext *disp = r->disp;
    int w = r->pending_w, h = r->pending_h, sc = r->pending_scale;
    if (disp && w > 0 && h > 0) r->pending_w = r->pending_h = 0;
    pthread_mutex_unlock(&r->disp_lock);
    if (!disp || w <= 0 || h <= 0) return;
    if (w == r->width && h == r->height) return;
    DISPLAY_CONTROL_MONITOR_LAYOUT layout;
    memset(&layout, 0, sizeof(layout));
    layout.Flags = DISPLAY_CONTROL_MONITOR_PRIMARY;
    layout.Width = (UINT32)(w & ~1);   // szerokość parzysta — wymóg protokołu
    layout.Height = (UINT32)h;
    layout.PhysicalWidth = layout.Width * 254 / 960;   // ok. 96 DPI w milimetrach
    layout.PhysicalHeight = layout.Height * 254 / 960;
    layout.Orientation = ORIENTATION_LANDSCAPE;
    layout.DesktopScaleFactor = (UINT32)(sc >= 100 ? sc : 100);
    layout.DeviceScaleFactor = sc >= 175 ? 180 : (sc >= 125 ? 140 : 100);
    (void)disp->SendMonitorLayout(disp, 1, &layout);
}

// --- Połączenie ---

static BOOL wp_pre_connect(freerdp *instance) {
    rdpSettings *s = instance->context->settings;
    if (!freerdp_settings_set_uint32(s, FreeRDP_OsMajorType, OSMAJORTYPE_MACINTOSH)) return FALSE;
    if (!freerdp_settings_set_uint32(s, FreeRDP_OsMinorType, OSMINORTYPE_MACINTOSH)) return FALSE;
    if (PubSub_SubscribeChannelConnected(instance->context->pubSub, on_channel_connected) < 0) return FALSE;
    if (PubSub_SubscribeChannelDisconnected(instance->context->pubSub, on_channel_disconnected) < 0) return FALSE;
    return TRUE;
}

static BOOL wp_post_connect(freerdp *instance) {
    rdpContext *context = instance->context;
    if (!gdi_init(instance, PIXEL_FORMAT_BGRA32)) return FALSE;
    context->update->BeginPaint = wp_begin_paint;
    context->update->EndPaint = wp_end_paint;
    context->update->DesktopResize = wp_desktop_resize;
    context->update->SetKeyboardIndicators = wp_keyboard_indicators;
    context->update->SetKeyboardImeStatus = wp_ime_status;
    context->update->PlaySound = wp_play_sound;
    wp_rdp *r = owner_of(context);
    if (r) {
        sync_size(r, context->gdi);
        notify_state(r, WP_RDP_CONNECTED, 0, NULL);
    }
    return TRUE;
}

static void wp_post_disconnect(freerdp *instance) {
    if (!instance || !instance->context) return;
    PubSub_UnsubscribeChannelConnected(instance->context->pubSub, on_channel_connected);
    PubSub_UnsubscribeChannelDisconnected(instance->context->pubSub, on_channel_disconnected);
    gdi_free(instance);
}

static DWORD verify_cert(freerdp *instance, const char *host, UINT16 port, const char *cn, const char *subject,
                         const char *issuer, const char *fp, int changed, const char *old_fp, DWORD flags) {
    wp_rdp *r = owner_of(instance->context);
    if (!r || !r->cb.verify_certificate) return 0;
    int answer = r->cb.verify_certificate(r->cb.user, host ? host : "", port, cn ? cn : "", subject ? subject : "",
                                          issuer ? issuer : "", fp ? fp : "", changed, old_fp ? old_fp : "",
                                          (flags & VERIFY_CERT_FLAG_GATEWAY) ? 1 : 0);
    return answer == WP_CERT_ACCEPT_ALWAYS ? 1 : (answer == WP_CERT_ACCEPT_ONCE ? 2 : 0);
}

static DWORD wp_verify_certificate(freerdp *instance, const char *host, UINT16 port, const char *cn,
                                   const char *subject, const char *issuer, const char *fp, DWORD flags) {
    return verify_cert(instance, host, port, cn, subject, issuer, fp, 0, NULL, flags);
}

static DWORD wp_verify_changed_certificate(freerdp *instance, const char *host, UINT16 port, const char *cn,
                                           const char *subject, const char *issuer, const char *new_fp,
                                           const char *old_subject, const char *old_issuer, const char *old_fp,
                                           DWORD flags) {
    (void)old_subject; (void)old_issuer;
    return verify_cert(instance, host, port, cn, subject, issuer, new_fp, 1, old_fp, flags);
}

static BOOL wp_authenticate(freerdp *instance, char **username, char **password, char **domain,
                            rdp_auth_reason reason) {
    wp_rdp *r = owner_of(instance->context);
    if (!r || !r->cb.authenticate) return FALSE;
    char u[256] = {0}, p[512] = {0}, d[256] = {0};
    if (*username) strncpy(u, *username, sizeof(u) - 1);
    if (*domain) strncpy(d, *domain, sizeof(d) - 1);
    int gateway = (reason == GW_AUTH_HTTP || reason == GW_AUTH_RDG || reason == GW_AUTH_RPC) ? 1 : 0;
    if (!r->cb.authenticate(r->cb.user, u, sizeof(u), p, sizeof(p), d, sizeof(d), gateway)) return FALSE;
    free(*username); free(*password); free(*domain);
    *username = strdup(u);
    *password = strdup(p);
    *domain = strdup(d);
    memset(p, 0, sizeof(p));
    return *username && *password && *domain;
}

static BOOL wp_client_new(freerdp *instance, rdpContext *context) {
    (void)context;
    instance->PreConnect = wp_pre_connect;
    instance->PostConnect = wp_post_connect;
    instance->PostDisconnect = wp_post_disconnect;
    instance->VerifyCertificateEx = wp_verify_certificate;
    instance->VerifyChangedCertificateEx = wp_verify_changed_certificate;
    instance->AuthenticateEx = wp_authenticate;
    return TRUE;
}

static void wp_client_free(freerdp *instance, rdpContext *context) { (void)instance; (void)context; }
static int wp_client_start(rdpContext *context) { (void)context; return 0; }
static int wp_client_stop(rdpContext *context) { (void)context; return 0; }

static BOOL global_init(void) { return TRUE; }
static void global_uninit(void) {}

static void *run(void *arg) {
    wp_rdp *r = (wp_rdp *)arg;
    freerdp *instance = r->context->instance;
    notify_state(r, WP_RDP_CONNECTING, 0, NULL);
    if (!freerdp_connect(instance)) {
        UINT32 err = freerdp_get_last_error(r->context);
        notify_state(r, WP_RDP_DISCONNECTED, err ? err : 1, freerdp_get_last_error_string(err));
        freerdp_disconnect(instance);
        return NULL;
    }
    while (!r->stopping && !freerdp_shall_disconnect_context(r->context)) {
        HANDLE handles[MAXIMUM_WAIT_OBJECTS];
        DWORD n = freerdp_get_event_handles(r->context, handles, ARRAYSIZE(handles));
        if (n == 0) break;
        // Krótki limit czasu: prośby o zmianę rozdzielczości i zatrzymanie idą bez osobnego zdarzenia.
        DWORD st = WaitForMultipleObjects(n, handles, FALSE, 100);
        if (st == WAIT_FAILED) break;
        if (!freerdp_check_event_handles(r->context)) {
            // Zerwane połączenie (sieć, uśpiony Mac, serwer w trakcie reaktywacji) — próby ponownego
            // połączenia jak w innych klientach FreeRDP; błędy logowania i przerwanie przez użytkownika
            // client_auto_reconnect_ex od razu odrzuca.
            if (!r->stopping && client_auto_reconnect_ex(instance, NULL)) continue;
            break;
        }
        send_pending_resize(r);
    }
    UINT32 err = r->stopping ? 0 : freerdp_get_last_error(r->context);
    // Wylogowanie i rozłączenie zlecone przez użytkownika (w sesji albo z innego połączenia) to nie błąd.
    if (err == FREERDP_ERROR_SUCCESS || err == MAKE_FREERDP_ERROR(ERRINFO, ERRINFO_LOGOFF_BY_USER)
        || err == MAKE_FREERDP_ERROR(ERRINFO, ERRINFO_RPC_INITIATED_LOGOFF)
        || err == MAKE_FREERDP_ERROR(ERRINFO, ERRINFO_RPC_INITIATED_DISCONNECT_BY_USER))
        err = 0;
    freerdp_disconnect(instance);
    notify_state(r, WP_RDP_DISCONNECTED, err, err ? freerdp_get_last_error_string(err) : "");
    return NULL;
}

int wp_rdp_available(void) { return 1; }

static BOOL set_str(rdpSettings *s, FreeRDP_Settings_Keys_String k, const char *v) {
    return (v && *v) ? freerdp_settings_set_string(s, k, v) : TRUE;
}

wp_rdp *wp_rdp_start(const wp_rdp_config *c, const wp_rdp_callbacks *cb) {
    static int log_once = 0;
    if (!log_once) {   // FreeRDP domyślnie pisze dużo na stdout — tylko błędy (WLOG_LEVEL nadpisuje)
        log_once = 1;
        if (!getenv("WLOG_LEVEL")) WLog_SetLogLevel(WLog_GetRoot(), WLOG_ERROR);
    }
    RDP_CLIENT_ENTRY_POINTS ep;
    memset(&ep, 0, sizeof(ep));
    ep.Version = RDP_CLIENT_INTERFACE_VERSION;
    ep.Size = sizeof(RDP_CLIENT_ENTRY_POINTS_V1);
    ep.GlobalInit = global_init;
    ep.GlobalUninit = global_uninit;
    ep.ContextSize = sizeof(wpContext);
    ep.ClientNew = wp_client_new;
    ep.ClientFree = wp_client_free;
    ep.ClientStart = wp_client_start;
    ep.ClientStop = wp_client_stop;

    wp_rdp *r = calloc(1, sizeof(wp_rdp));
    if (!r) return NULL;
    r->cb = *cb;
    pthread_mutex_init(&r->frame_lock, NULL);
    pthread_mutex_init(&r->disp_lock, NULL);
    r->context = freerdp_client_context_new(&ep);
    if (!r->context) { free(r); return NULL; }
    ((wpContext *)r->context)->owner = r;

    rdpSettings *s = r->context->settings;
    BOOL ok = set_str(s, FreeRDP_ServerHostname, c->host)
        && freerdp_settings_set_uint32(s, FreeRDP_ServerPort, c->port ? c->port : 3389)
        && set_str(s, FreeRDP_Username, c->username)
        && set_str(s, FreeRDP_Domain, c->domain)
        && set_str(s, FreeRDP_Password, c->password)
        && freerdp_settings_set_uint32(s, FreeRDP_DesktopWidth, (UINT32)((c->width > 0 ? c->width : 1280) & ~1))
        && freerdp_settings_set_uint32(s, FreeRDP_DesktopHeight, (UINT32)(c->height > 0 ? c->height : 800))
        && freerdp_settings_set_uint32(s, FreeRDP_ColorDepth, 32)
        && freerdp_settings_set_bool(s, FreeRDP_SupportDisplayControl, TRUE)
        && freerdp_settings_set_bool(s, FreeRDP_DynamicResolutionUpdate, TRUE)
        && freerdp_settings_set_bool(s, FreeRDP_SupportGraphicsPipeline, TRUE)
        && freerdp_settings_set_bool(s, FreeRDP_RedirectClipboard, c->redirect_clipboard ? TRUE : FALSE)
        && freerdp_settings_set_bool(s, FreeRDP_ConsoleSession, c->admin_session ? TRUE : FALSE)
        && freerdp_settings_set_bool(s, FreeRDP_AutoReconnectionEnabled, TRUE)
        && freerdp_settings_set_bool(s, FreeRDP_IgnoreCertificate, c->ignore_certificate ? TRUE : FALSE)
        && set_str(s, FreeRDP_ConfigPath, c->config_dir)
        && set_str(s, FreeRDP_ClientHostname, c->client_hostname);
    if (ok && c->scale_percent > 100) {
        ok = freerdp_settings_set_uint32(s, FreeRDP_DesktopScaleFactor, (UINT32)c->scale_percent)
            && freerdp_settings_set_uint32(s, FreeRDP_DeviceScaleFactor,
                                           c->scale_percent >= 175 ? 180 : (c->scale_percent >= 125 ? 140 : 100));
    }
    if (ok && c->gateway_host && *c->gateway_host) {
        ok = freerdp_settings_set_bool(s, FreeRDP_GatewayEnabled, TRUE)
            && set_str(s, FreeRDP_GatewayHostname, c->gateway_host)
            && freerdp_settings_set_uint32(s, FreeRDP_GatewayPort, c->gateway_port ? c->gateway_port : 443)
            && freerdp_settings_set_bool(s, FreeRDP_GatewayUseSameCredentials, c->gateway_same_credentials ? TRUE : FALSE)
            && set_str(s, FreeRDP_GatewayUsername, c->gateway_username)
            && set_str(s, FreeRDP_GatewayDomain, c->gateway_domain)
            && set_str(s, FreeRDP_GatewayPassword, c->gateway_password);
    }
    if (!ok || pthread_create(&r->thread, NULL, run, r) != 0) {
        freerdp_client_context_free(r->context);
        pthread_mutex_destroy(&r->frame_lock);
        pthread_mutex_destroy(&r->disp_lock);
        free(r);
        return NULL;
    }
    r->thread_started = 1;
    return r;
}

void wp_rdp_stop(wp_rdp *r) {
    if (!r) return;
    r->stopping = 1;
    if (r->context) freerdp_abort_connect_context(r->context);
    if (r->thread_started) pthread_join(r->thread, NULL);
    freerdp_client_context_free(r->context);
    pthread_mutex_lock(&r->frame_lock);
    free(r->frame);
    r->frame = NULL;
    pthread_mutex_unlock(&r->frame_lock);
    pthread_mutex_destroy(&r->frame_lock);
    pthread_mutex_destroy(&r->disp_lock);
    free(r);
}

int wp_rdp_lock_frame(wp_rdp *r, const uint8_t **data, int *width, int *height, int *stride) {
    pthread_mutex_lock(&r->frame_lock);
    *data = r->frame;
    *width = r->width;
    *height = r->height;
    *stride = r->stride;
    return r->frame != NULL;
}

void wp_rdp_unlock_frame(wp_rdp *r) { pthread_mutex_unlock(&r->frame_lock); }

static rdpInput *input_of(wp_rdp *r) {
    if (!r || r->stopping || !r->context || !r->context->input || !r->context->gdi) return NULL;
    return r->context->input;
}

void wp_rdp_key(wp_rdp *r, int down, uint32_t scancode) {
    rdpInput *in = input_of(r);
    if (in) (void)freerdp_input_send_keyboard_event_ex(in, down ? TRUE : FALSE, FALSE, scancode);
}

void wp_rdp_unicode(wp_rdp *r, int down, uint16_t code) {
    rdpInput *in = input_of(r);
    if (in) (void)freerdp_input_send_unicode_keyboard_event(in, down ? 0 : KBD_FLAGS_RELEASE, code);
}

static UINT16 clamp(int v, int max) { return (UINT16)(v < 0 ? 0 : (v >= max ? (max > 0 ? max - 1 : 0) : v)); }

void wp_rdp_mouse(wp_rdp *r, uint16_t flags, int x, int y) {
    rdpInput *in = input_of(r);
    if (in) (void)freerdp_input_send_mouse_event(in, flags, clamp(x, r->width), clamp(y, r->height));
}

void wp_rdp_mouse_extended(wp_rdp *r, uint16_t flags, int x, int y) {
    rdpInput *in = input_of(r);
    if (in) (void)freerdp_input_send_extended_mouse_event(in, flags, clamp(x, r->width), clamp(y, r->height));
}

void wp_rdp_resize(wp_rdp *r, int width, int height, int scale_percent) {
    if (!r) return;
    pthread_mutex_lock(&r->disp_lock);
    r->pending_w = width;
    r->pending_h = height;
    r->pending_scale = scale_percent;
    pthread_mutex_unlock(&r->disp_lock);
}

void wp_rdp_sync_locks(wp_rdp *r, uint32_t flags) {
    rdpInput *in = input_of(r);
    if (in) (void)freerdp_input_send_synchronize_event(in, flags);
}

int wp_rdp_error_is_auth(uint32_t e) {
    return e == FREERDP_ERROR_AUTHENTICATION_FAILED || e == FREERDP_ERROR_CONNECT_LOGON_FAILURE
        || e == FREERDP_ERROR_CONNECT_WRONG_PASSWORD || e == FREERDP_ERROR_CONNECT_NO_OR_MISSING_CREDENTIALS;
}

uint32_t wp_rdp_scancode_from_mac_keycode(uint32_t keycode) {
    DWORD vk = GetVirtualKeyCodeFromKeycode(keycode, WINPR_KEYCODE_TYPE_APPLE);
    if (!vk) return 0;
    return GetVirtualScanCodeFromVirtualKeyCode(vk, WINPR_KBD_TYPE_IBM_ENHANCED);
}

#endif
