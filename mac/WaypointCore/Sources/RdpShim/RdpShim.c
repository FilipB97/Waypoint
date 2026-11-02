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
void wp_rdp_set_clipboard_text(wp_rdp *r, const char *t) { (void)r; (void)t; }
void wp_rdp_set_clipboard_files(wp_rdp *r, int c, const char *const *n, const char *const *p, const int64_t *s, const int *d) { (void)r; (void)c; (void)n; (void)p; (void)s; (void)d; }
int wp_rdp_clipboard_request_file(wp_rdp *r, uint32_t s, int i, int z, uint64_t p, uint32_t l) { (void)r; (void)s; (void)i; (void)z; (void)p; (void)l; return 0; }
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
#include <freerdp/client/cliprdr.h>
#include <freerdp/channels/cliprdr.h>
#include <freerdp/utils/cliprdr_utils.h>
#include <freerdp/client/cmdline.h>
#include <freerdp/gdi/gdi.h>
#include <freerdp/graphics.h>
#include <freerdp/codec/color.h>
#include <freerdp/codecs.h>
#include <freerdp/input.h>
#include <freerdp/error.h>
#include <freerdp/log.h>
#include <winpr/synch.h>
#include <winpr/input.h>
#include <winpr/string.h>
#include <winpr/user.h>
#include <winpr/shell.h>
#include <fcntl.h>
#include <unistd.h>

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

    // Schowek (tekst i pliki) — pod disp_lock. Wysyłanie z wątku połączenia, budzonego zdarzeniem `wake`.
    HANDLE wake;
    CliprdrClientContext *clip;
    int clip_ready;            // po MonitorReady — wcześniej serwer nie przyjmie listy formatów
    int clip_pending;          // zawartość z Maca zmieniła się, trzeba ogłosić nową listę formatów
    UINT32 server_flags;       // generalFlags serwera (pliki strumieniowo, duże pliki)
    char *clip_text;           // tekst z Maca (UTF-8) — oddawany na prośbę serwera
    int lf_count;              // pliki z Maca: nazwy względne (z '\\'), ścieżki, rozmiary, katalogi
    char **lf_names, **lf_paths;
    int64_t *lf_sizes;
    int *lf_dirs;
    UINT32 srv_files_format;   // id formatu FileGroupDescriptorW na serwerze (0 = brak plików)
    UINT32 requested_format;   // na co czekamy w odpowiedzi na prośbę o dane
    int req_pending;           // prośba o fragment pliku z serwera (jedna naraz)
    CLIPRDR_FILE_CONTENTS_REQUEST req;

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

// --- Schowek (cliprdr): tekst Unicode i pliki (FileGroupDescriptorW + FileContents) ---

#define WP_FILES_FORMAT_ID 0xC0BC          // nasz identyfikator formatu listy plików (dowolny >= 0xC000)
#define WP_FILES_FORMAT_NAME "FileGroupDescriptorW"

static void free_local_files(wp_rdp *r) {
    for (int i = 0; i < r->lf_count; i++) { free(r->lf_names[i]); free(r->lf_paths[i]); }
    free(r->lf_names); free(r->lf_paths); free(r->lf_sizes); free(r->lf_dirs);
    r->lf_names = r->lf_paths = NULL; r->lf_sizes = NULL; r->lf_dirs = NULL; r->lf_count = 0;
}

static UINT clip_send_format_list(CliprdrClientContext *clip, int has_text, int has_files) {
    CLIPRDR_FORMAT fmts[2];
    UINT32 n = 0;
    if (has_files) fmts[n++] = (CLIPRDR_FORMAT){ .formatId = WP_FILES_FORMAT_ID, .formatName = WP_FILES_FORMAT_NAME };
    if (has_text) fmts[n++] = (CLIPRDR_FORMAT){ .formatId = CF_UNICODETEXT, .formatName = NULL };
    CLIPRDR_FORMAT_LIST list = { 0 };
    list.common.msgType = CB_FORMAT_LIST;
    list.numFormats = n;
    list.formats = n ? fmts : NULL;
    return clip->ClientFormatList(clip, &list);
}

static UINT clip_request_data(wp_rdp *r, CliprdrClientContext *clip, UINT32 format) {
    pthread_mutex_lock(&r->disp_lock);
    r->requested_format = format;
    pthread_mutex_unlock(&r->disp_lock);
    CLIPRDR_FORMAT_DATA_REQUEST req = { 0 };
    req.common.msgType = CB_FORMAT_DATA_REQUEST;
    req.requestedFormatId = format;
    return clip->ClientFormatDataRequest(clip, &req);
}

static UINT clip_monitor_ready(CliprdrClientContext *clip, const CLIPRDR_MONITOR_READY *m) {
    (void)m;
    wp_rdp *r = (wp_rdp *)clip->custom;
    CLIPRDR_GENERAL_CAPABILITY_SET general = { .capabilitySetType = CB_CAPSTYPE_GENERAL,
        .capabilitySetLength = CB_CAPSTYPE_GENERAL_LEN, .version = CB_CAPS_VERSION_2,
        .generalFlags = CB_USE_LONG_FORMAT_NAMES | CB_STREAM_FILECLIP_ENABLED | CB_FILECLIP_NO_FILE_PATHS
                        | CB_HUGE_FILE_SUPPORT_ENABLED };
    CLIPRDR_CAPABILITIES caps = { 0 };
    caps.common.msgType = CB_CLIP_CAPS;
    caps.cCapabilitiesSets = 1;
    caps.capabilitySets = (CLIPRDR_CAPABILITY_SET *)&general;
    UINT rc = clip->ClientCapabilities(clip, &caps);
    if (rc != CHANNEL_RC_OK) return rc;
    pthread_mutex_lock(&r->disp_lock);
    int has_text = r->clip_text != NULL, has_files = r->lf_count > 0;
    r->clip_ready = 1;
    r->clip_pending = 0;
    pthread_mutex_unlock(&r->disp_lock);
    return clip_send_format_list(clip, has_text, has_files);
}

static UINT clip_server_caps(CliprdrClientContext *clip, const CLIPRDR_CAPABILITIES *c) {
    wp_rdp *r = (wp_rdp *)clip->custom;
    const BYTE *p = (const BYTE *)c->capabilitySets;
    for (UINT32 i = 0; i < c->cCapabilitiesSets; i++) {
        const CLIPRDR_CAPABILITY_SET *set = (const CLIPRDR_CAPABILITY_SET *)p;
        if (set->capabilitySetType == CB_CAPSTYPE_GENERAL) {
            pthread_mutex_lock(&r->disp_lock);
            r->server_flags = ((const CLIPRDR_GENERAL_CAPABILITY_SET *)set)->generalFlags;
            pthread_mutex_unlock(&r->disp_lock);
        }
        p += sizeof(CLIPRDR_GENERAL_CAPABILITY_SET);   // FreeRDP rozpakowuje zestawy do tablicy tej struktury
    }
    return CHANNEL_RC_OK;
}

static UINT clip_server_format_list(CliprdrClientContext *clip, const CLIPRDR_FORMAT_LIST *list) {
    wp_rdp *r = (wp_rdp *)clip->custom;
    CLIPRDR_FORMAT_LIST_RESPONSE resp = { 0 };
    resp.common.msgType = CB_FORMAT_LIST_RESPONSE;
    resp.common.msgFlags = CB_RESPONSE_OK;
    UINT rc = clip->ClientFormatListResponse(clip, &resp);
    if (rc != CHANNEL_RC_OK) return rc;
    UINT32 files = 0;
    int text = 0;
    for (UINT32 i = 0; i < list->numFormats; i++) {
        const CLIPRDR_FORMAT *f = &list->formats[i];
        if (f->formatName && strcmp(f->formatName, WP_FILES_FORMAT_NAME) == 0) files = f->formatId;
        if (f->formatId == CF_UNICODETEXT) text = 1;
    }
    pthread_mutex_lock(&r->disp_lock);
    r->srv_files_format = files;
    pthread_mutex_unlock(&r->disp_lock);
    // Pliki mają pierwszeństwo (Eksplorator dokłada też nazwy jako tekst). Od razu pobieramy listę
    // plików / tekst — schowek Maca nie ma „leniwego" dostarczania między procesami.
    if (files) return clip_request_data(r, clip, files);
    if (text) return clip_request_data(r, clip, CF_UNICODETEXT);
    return CHANNEL_RC_OK;
}

static UINT clip_server_format_list_response(CliprdrClientContext *clip, const CLIPRDR_FORMAT_LIST_RESPONSE *r) {
    (void)clip; (void)r;
    return CHANNEL_RC_OK;
}

/// Lista plików z Maca w formacie FileGroupDescriptorW.
static BOOL build_file_list(wp_rdp *r, BYTE **out, UINT32 *out_len) {
    FILEDESCRIPTORW *d = calloc((size_t)r->lf_count, sizeof(FILEDESCRIPTORW));
    if (!d) return FALSE;
    for (int i = 0; i < r->lf_count; i++) {
        d[i].dwFlags = FD_ATTRIBUTES | FD_FILESIZE | FD_PROGRESSUI;
        d[i].dwFileAttributes = r->lf_dirs[i] ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        UINT64 size = r->lf_dirs[i] ? 0 : (UINT64)r->lf_sizes[i];
        d[i].nFileSizeHigh = (DWORD)(size >> 32);
        d[i].nFileSizeLow = (DWORD)(size & 0xFFFFFFFF);
        (void)ConvertUtf8ToWChar(r->lf_names[i], d[i].cFileName, ARRAYSIZE(d[i].cFileName));
    }
    UINT32 flags = CB_STREAM_FILECLIP_ENABLED | (r->server_flags & CB_HUGE_FILE_SUPPORT_ENABLED);
    UINT rc = cliprdr_serialize_file_list_ex(flags, d, (UINT32)r->lf_count, out, out_len);
    free(d);
    return rc == CHANNEL_RC_OK;
}

static UINT clip_server_data_request(CliprdrClientContext *clip, const CLIPRDR_FORMAT_DATA_REQUEST *req) {
    wp_rdp *r = (wp_rdp *)clip->custom;
    BYTE *data = NULL;
    UINT32 len = 0;
    pthread_mutex_lock(&r->disp_lock);
    if (req->requestedFormatId == CF_UNICODETEXT && r->clip_text) {
        size_t chars = 0;
        WCHAR *w = ConvertUtf8ToWCharAlloc(r->clip_text, &chars);
        if (w) { data = (BYTE *)w; len = (UINT32)((chars + 1) * sizeof(WCHAR)); }   // z kończącym zerem
    } else if (req->requestedFormatId == WP_FILES_FORMAT_ID && r->lf_count > 0) {
        if (!build_file_list(r, &data, &len)) { data = NULL; len = 0; }
    }
    pthread_mutex_unlock(&r->disp_lock);
    CLIPRDR_FORMAT_DATA_RESPONSE resp = { 0 };
    resp.common.msgType = CB_FORMAT_DATA_RESPONSE;
    resp.common.msgFlags = data ? CB_RESPONSE_OK : CB_RESPONSE_FAIL;
    resp.common.dataLen = data ? len : 0;
    resp.requestedFormatData = data;
    UINT rc = clip->ClientFormatDataResponse(clip, &resp);
    free(data);
    return rc;
}

static UINT clip_server_data_response(CliprdrClientContext *clip, const CLIPRDR_FORMAT_DATA_RESPONSE *resp) {
    wp_rdp *r = (wp_rdp *)clip->custom;
    pthread_mutex_lock(&r->disp_lock);
    UINT32 format = r->requested_format;
    UINT32 files_format = r->srv_files_format;
    pthread_mutex_unlock(&r->disp_lock);
    if (!(resp->common.msgFlags & CB_RESPONSE_OK) || !resp->requestedFormatData) return CHANNEL_RC_OK;

    if (format != 0 && format == files_format) {
        FILEDESCRIPTORW *d = NULL;
        UINT32 count = 0;
        if (cliprdr_parse_file_list(resp->requestedFormatData, resp->common.dataLen, &d, &count) != CHANNEL_RC_OK)
            return CHANNEL_RC_OK;
        char **names = calloc(count ? count : 1, sizeof(char *));
        int64_t *sizes = calloc(count ? count : 1, sizeof(int64_t));
        int *dirs = calloc(count ? count : 1, sizeof(int));
        if (names && sizes && dirs) {
            for (UINT32 i = 0; i < count; i++) {
                names[i] = ConvertWCharNToUtf8Alloc(d[i].cFileName, _wcsnlen(d[i].cFileName, ARRAYSIZE(d[i].cFileName)), NULL);
                if (names[i]) for (char *c = names[i]; *c; c++) if (*c == '\\') *c = '/';
                dirs[i] = (d[i].dwFlags & FD_ATTRIBUTES) && (d[i].dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY);
                sizes[i] = (d[i].dwFlags & FD_FILESIZE)
                    ? (int64_t)(((UINT64)d[i].nFileSizeHigh << 32) | d[i].nFileSizeLow) : -1;
            }
            if (r->cb.clipboard_files) r->cb.clipboard_files(r->cb.user, (int)count, (const char *const *)names, sizes, dirs);
            for (UINT32 i = 0; i < count; i++) free(names[i]);
        }
        free(names); free(sizes); free(dirs); free(d);
        return CHANNEL_RC_OK;
    }
    if (resp->common.dataLen < 2) return CHANNEL_RC_OK;
    size_t wlen = resp->common.dataLen / sizeof(WCHAR);
    const WCHAR *w = (const WCHAR *)resp->requestedFormatData;
    while (wlen > 0 && w[wlen - 1] == 0) wlen--;
    char *utf8 = ConvertWCharNToUtf8Alloc(w, wlen, NULL);
    if (utf8 && r->cb.clipboard_text) r->cb.clipboard_text(r->cb.user, utf8);
    free(utf8);
    return CHANNEL_RC_OK;
}

/// Serwer czyta plik z Maca (wklejenie w Eksploratorze): rozmiar albo fragment.
static UINT clip_server_file_request(CliprdrClientContext *clip, const CLIPRDR_FILE_CONTENTS_REQUEST *req) {
    wp_rdp *r = (wp_rdp *)clip->custom;
    CLIPRDR_FILE_CONTENTS_RESPONSE resp = { 0 };
    resp.common.msgType = CB_FILECONTENTS_RESPONSE;
    resp.streamId = req->streamId;
    BYTE *buf = NULL;
    UINT64 size_le = 0;
    char *path = NULL;
    int64_t fsize = -1;
    pthread_mutex_lock(&r->disp_lock);
    if (req->listIndex < (UINT32)r->lf_count && !r->lf_dirs[req->listIndex]) {
        path = strdup(r->lf_paths[req->listIndex]);
        fsize = r->lf_sizes[req->listIndex];
    }
    pthread_mutex_unlock(&r->disp_lock);
    if (path && (req->dwFlags & FILECONTENTS_SIZE)) {
        size_le = (UINT64)fsize;
        resp.common.msgFlags = CB_RESPONSE_OK;
        resp.cbRequested = 8;
        resp.requestedData = (const BYTE *)&size_le;   // little-endian (x86/arm)
    } else if (path && (req->dwFlags & FILECONTENTS_RANGE)) {
        UINT64 pos = ((UINT64)req->nPositionHigh << 32) | req->nPositionLow;
        UINT32 want = req->cbRequested > (8u << 20) ? (8u << 20) : req->cbRequested;
        buf = malloc(want ? want : 1);
        int fd = open(path, O_RDONLY);
        ssize_t got = (buf && fd >= 0) ? pread(fd, buf, want, (off_t)pos) : -1;
        if (fd >= 0) close(fd);
        if (got >= 0) {
            resp.common.msgFlags = CB_RESPONSE_OK;
            resp.cbRequested = (UINT32)got;
            resp.requestedData = buf;
        } else {
            resp.common.msgFlags = CB_RESPONSE_FAIL;
        }
    } else {
        resp.common.msgFlags = CB_RESPONSE_FAIL;
    }
    UINT rc = clip->ClientFileContentsResponse(clip, &resp);
    free(buf);
    free(path);
    return rc;
}

/// Odpowiedź serwera na naszą prośbę o fragment pliku (wklejenie w Finderze).
static UINT clip_server_file_response(CliprdrClientContext *clip, const CLIPRDR_FILE_CONTENTS_RESPONSE *resp) {
    wp_rdp *r = (wp_rdp *)clip->custom;
    int ok = (resp->common.msgFlags & CB_RESPONSE_OK) != 0;
    if (r->cb.clipboard_file_data)
        r->cb.clipboard_file_data(r->cb.user, resp->streamId, ok, ok ? resp->requestedData : NULL, ok ? resp->cbRequested : 0);
    return CHANNEL_RC_OK;
}

static void clip_attach(wp_rdp *r, CliprdrClientContext *clip) {
    clip->custom = r;
    clip->MonitorReady = clip_monitor_ready;
    clip->ServerCapabilities = clip_server_caps;
    clip->ServerFormatList = clip_server_format_list;
    clip->ServerFormatListResponse = clip_server_format_list_response;
    clip->ServerFormatDataRequest = clip_server_data_request;
    clip->ServerFormatDataResponse = clip_server_data_response;
    clip->ServerFileContentsRequest = clip_server_file_request;
    clip->ServerFileContentsResponse = clip_server_file_response;
    pthread_mutex_lock(&r->disp_lock);
    r->clip = clip;
    r->clip_ready = 0;
    pthread_mutex_unlock(&r->disp_lock);
}

/// Nowa zawartość z Maca → lista formatów; prośba o fragment pliku → serwer (wątek połączenia).
static void send_pending_clipboard(wp_rdp *r) {
    pthread_mutex_lock(&r->disp_lock);
    CliprdrClientContext *clip = r->clip_ready ? r->clip : NULL;
    int list = clip && r->clip_pending;
    int has_text = r->clip_text != NULL, has_files = r->lf_count > 0;
    if (list) r->clip_pending = 0;
    int req = clip && r->req_pending;
    CLIPRDR_FILE_CONTENTS_REQUEST fr = r->req;
    if (req) r->req_pending = 0;
    pthread_mutex_unlock(&r->disp_lock);
    if (list) (void)clip_send_format_list(clip, has_text, has_files);
    if (req && clip->ClientFileContentsRequest(clip, &fr) != CHANNEL_RC_OK && r->cb.clipboard_file_data)
        r->cb.clipboard_file_data(r->cb.user, fr.streamId, 0, NULL, 0);
}

// --- Kanały ---

static void on_channel_connected(void *context, const ChannelConnectedEventArgs *e) {
    wpContext *wc = (wpContext *)context;
    if (strcmp(e->name, DISP_DVC_CHANNEL_NAME) == 0) {
        pthread_mutex_lock(&wc->owner->disp_lock);
        wc->owner->disp = (DispClientContext *)e->pInterface;
        pthread_mutex_unlock(&wc->owner->disp_lock);
    } else if (strcmp(e->name, CLIPRDR_SVC_CHANNEL_NAME) == 0) {
        clip_attach(wc->owner, (CliprdrClientContext *)e->pInterface);
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
    } else if (strcmp(e->name, CLIPRDR_SVC_CHANNEL_NAME) == 0) {
        pthread_mutex_lock(&wc->owner->disp_lock);
        wc->owner->clip = NULL;
        wc->owner->clip_ready = 0;
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

// --- Kursor z sesji (zajęty, ładowanie, kursor tekstowy, zmiana rozmiaru…) ---

typedef struct {
    rdpPointer pointer;        // musi być pierwsze — FreeRDP alokuje `size` bajtów i rzutuje
    uint8_t *bgra;
} wpPointer;

/// Nigdy nie zwraca FALSE: błąd tutaj FreeRDP traktuje jak błąd protokołu i zrywa sesję (Windows
/// wysyła np. na ekranie logowania kursory bez maski, których konwersja się nie udaje). Kursor, którego
/// nie umiemy pokazać, staje się zwykłą strzałką.
static BOOL wp_pointer_new(rdpContext *context, rdpPointer *pointer) {
    wpPointer *p = (wpPointer *)pointer;
    p->bgra = NULL;
    size_t size = (size_t)pointer->width * pointer->height * 4;
    if (size == 0 || pointer->width > 512 || pointer->height > 512) return TRUE;
    uint8_t *bgra = calloc(1, size);
    if (!bgra) return TRUE;
    if (freerdp_image_copy_from_pointer_data(bgra, PIXEL_FORMAT_BGRA32, pointer->width * 4, 0, 0,
                                             pointer->width, pointer->height, pointer->xorMaskData,
                                             pointer->lengthXorMask, pointer->andMaskData, pointer->lengthAndMask,
                                             pointer->xorBpp, context->gdi ? &context->gdi->palette : NULL))
        p->bgra = bgra;
    else
        free(bgra);
    return TRUE;
}

static void wp_pointer_free(rdpContext *context, rdpPointer *pointer) {
    (void)context;
    wpPointer *p = (wpPointer *)pointer;
    free(p->bgra);
    p->bgra = NULL;
}

static BOOL wp_pointer_set(rdpContext *context, rdpPointer *pointer) {
    wp_rdp *r = owner_of(context);
    wpPointer *p = (wpPointer *)pointer;
    if (!r || !r->cb.cursor) return TRUE;
    if (p->bgra)
        r->cb.cursor(r->cb.user, WP_CURSOR_IMAGE, (int)pointer->width, (int)pointer->height,
                     (int)pointer->xPos, (int)pointer->yPos, p->bgra);
    else
        r->cb.cursor(r->cb.user, WP_CURSOR_DEFAULT, 0, 0, 0, 0, NULL);
    return TRUE;
}

static BOOL wp_pointer_set_null(rdpContext *context) {
    wp_rdp *r = owner_of(context);
    if (r && r->cb.cursor) r->cb.cursor(r->cb.user, WP_CURSOR_HIDDEN, 0, 0, 0, 0, NULL);
    return TRUE;
}

static BOOL wp_pointer_set_default(rdpContext *context) {
    wp_rdp *r = owner_of(context);
    if (r && r->cb.cursor) r->cb.cursor(r->cb.user, WP_CURSOR_DEFAULT, 0, 0, 0, 0, NULL);
    return TRUE;
}

static BOOL wp_pointer_set_position(rdpContext *context, UINT32 x, UINT32 y) {
    (void)context; (void)x; (void)y;   // mysz należy do Maca — serwer nie przesuwa kursora
    return TRUE;
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
    rdpPointer pointer = { 0 };
    pointer.size = sizeof(wpPointer);
    pointer.New = wp_pointer_new;
    pointer.Free = wp_pointer_free;
    pointer.Set = wp_pointer_set;
    pointer.SetNull = wp_pointer_set_null;
    pointer.SetDefault = wp_pointer_set_default;
    pointer.SetPosition = wp_pointer_set_position;
    graphics_register_pointer(context->graphics, &pointer);
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
        DWORD n = freerdp_get_event_handles(r->context, handles, ARRAYSIZE(handles) - 1);
        if (n == 0) break;
        handles[n++] = r->wake;   // schowek z Maca / prośba o fragment pliku — bez czekania na limit
        // Krótki limit czasu: prośby o zmianę rozdzielczości i zatrzymanie idą bez osobnego zdarzenia.
        DWORD st = WaitForMultipleObjects(n, handles, FALSE, 100);
        if (st == WAIT_FAILED) break;
        ResetEvent(r->wake);
        if (!freerdp_check_event_handles(r->context)) {
            // Zerwane połączenie (sieć, uśpiony Mac, serwer w trakcie reaktywacji) — próby ponownego
            // połączenia jak w innych klientach FreeRDP; błędy logowania i przerwanie przez użytkownika
            // client_auto_reconnect_ex od razu odrzuca.
            if (!r->stopping && client_auto_reconnect_ex(instance, NULL)) continue;
            break;
        }
        send_pending_resize(r);
        send_pending_clipboard(r);
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
    r->wake = CreateEvent(NULL, TRUE, FALSE, NULL);
    r->context = r->wake ? freerdp_client_context_new(&ep) : NULL;
    if (!r->context) { if (r->wake) (void)CloseHandle(r->wake); free(r); return NULL; }
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
        // Pełne wrażenia jak mstsc w sieci LAN (flagi wydajności w Client Info). Domyślnie FreeRDP wyłącza
        // kompozycję pulpitu i animacje — Windows rysuje wtedy uproszczony interfejs: bez podświetleń,
        // przejść i animowanych wskaźników ładowania (Fluent/WinUI korzystają z kompozycji).
        && freerdp_settings_set_bool(s, FreeRDP_AllowFontSmoothing, TRUE)
        && freerdp_settings_set_bool(s, FreeRDP_AllowDesktopComposition, TRUE)
        && freerdp_settings_set_bool(s, FreeRDP_DisableWallpaper, FALSE)
        && freerdp_settings_set_bool(s, FreeRDP_DisableFullWindowDrag, FALSE)
        && freerdp_settings_set_bool(s, FreeRDP_DisableMenuAnims, FALSE)
        && freerdp_settings_set_bool(s, FreeRDP_DisableThemes, FALSE)
        && freerdp_settings_set_bool(s, FreeRDP_DisableCursorShadow, FALSE)
        && freerdp_settings_set_bool(s, FreeRDP_DisableCursorBlinking, FALSE)
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
    if (ok && c->drive_path && *c->drive_path) {
        const char *params[] = { "drive", (c->drive_name && *c->drive_name) ? c->drive_name : "Mac", c->drive_path };
        ok = freerdp_settings_set_bool(s, FreeRDP_DeviceRedirection, TRUE)
            && freerdp_settings_set_bool(s, FreeRDP_RedirectDrives, TRUE)
            && freerdp_client_add_device_channel(s, 3, params);
    }
    if (!ok || pthread_create(&r->thread, NULL, run, r) != 0) {
        freerdp_client_context_free(r->context);
        pthread_mutex_destroy(&r->frame_lock);
        pthread_mutex_destroy(&r->disp_lock);
        (void)CloseHandle(r->wake);
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
    free(r->clip_text);
    free_local_files(r);
    (void)CloseHandle(r->wake);
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

void wp_rdp_set_clipboard_text(wp_rdp *r, const char *utf8) {
    if (!r) return;
    char *copy = (utf8 && *utf8) ? strdup(utf8) : NULL;
    pthread_mutex_lock(&r->disp_lock);
    free(r->clip_text);
    r->clip_text = copy;
    free_local_files(r);
    r->clip_pending = 1;
    pthread_mutex_unlock(&r->disp_lock);
    SetEvent(r->wake);
}

void wp_rdp_set_clipboard_files(wp_rdp *r, int count, const char *const *names, const char *const *paths,
                                const int64_t *sizes, const int *is_dir) {
    if (!r) return;
    pthread_mutex_lock(&r->disp_lock);
    free(r->clip_text);
    r->clip_text = NULL;
    free_local_files(r);
    if (count > 0) {
        r->lf_names = calloc((size_t)count, sizeof(char *));
        r->lf_paths = calloc((size_t)count, sizeof(char *));
        r->lf_sizes = calloc((size_t)count, sizeof(int64_t));
        r->lf_dirs = calloc((size_t)count, sizeof(int));
        if (r->lf_names && r->lf_paths && r->lf_sizes && r->lf_dirs) {
            for (int i = 0; i < count; i++) {
                r->lf_names[i] = strdup(names[i]);
                for (char *c = r->lf_names[i]; c && *c; c++) if (*c == '/') *c = '\\';
                r->lf_paths[i] = strdup(paths[i] ? paths[i] : "");
                r->lf_sizes[i] = sizes[i];
                r->lf_dirs[i] = is_dir[i];
            }
            r->lf_count = count;
        }
    }
    r->clip_pending = 1;
    pthread_mutex_unlock(&r->disp_lock);
    SetEvent(r->wake);
}

int wp_rdp_clipboard_request_file(wp_rdp *r, uint32_t stream_id, int index, int size_only,
                                  uint64_t position, uint32_t length) {
    if (!r) return 0;
    pthread_mutex_lock(&r->disp_lock);
    int ok = r->clip_ready && r->srv_files_format != 0 && !r->req_pending;
    if (ok) {
        CLIPRDR_FILE_CONTENTS_REQUEST q = { 0 };
        q.common.msgType = CB_FILECONTENTS_REQUEST;
        q.streamId = stream_id;
        q.listIndex = (UINT32)index;
        q.dwFlags = size_only ? FILECONTENTS_SIZE : FILECONTENTS_RANGE;
        q.nPositionLow = size_only ? 0 : (UINT32)(position & 0xFFFFFFFF);
        q.nPositionHigh = size_only ? 0 : (UINT32)(position >> 32);
        q.cbRequested = size_only ? 8 : length;
        r->req = q;
        r->req_pending = 1;
    }
    pthread_mutex_unlock(&r->disp_lock);
    if (ok) SetEvent(r->wake);
    return ok;
}

#endif
