// Cienka nakładka na libcurl dla Swifta: curl_easy_setopt/getinfo są funkcjami o zmiennej liczbie
// argumentów, których Swift nie wywoła bezpośrednio — tu mają typowane odpowiedniki.
#ifndef WAYPOINT_CURL_SHIM_H
#define WAYPOINT_CURL_SHIM_H

#include <curl/curl.h>

typedef size_t (*wp_data_cb)(char *ptr, size_t size, size_t nmemb, void *userdata);
typedef int (*wp_progress_cb)(void *userdata, curl_off_t dltotal, curl_off_t dlnow, curl_off_t ultotal, curl_off_t ulnow);

CURLcode wp_set_long(CURL *h, CURLoption opt, long v);
CURLcode wp_set_off(CURL *h, CURLoption opt, curl_off_t v);
CURLcode wp_set_str(CURL *h, CURLoption opt, const char *v);
CURLcode wp_set_ptr(CURL *h, CURLoption opt, void *v);
CURLcode wp_set_list(CURL *h, CURLoption opt, struct curl_slist *v);
CURLcode wp_set_write(CURL *h, wp_data_cb cb, void *userdata);
CURLcode wp_set_read(CURL *h, wp_data_cb cb, void *userdata);
CURLcode wp_set_progress(CURL *h, wp_progress_cb cb, void *userdata);

CURLcode wp_get_long(CURL *h, CURLINFO info, long *out);
CURLcode wp_get_off(CURL *h, CURLINFO info, curl_off_t *out);
CURLcode wp_get_str(CURL *h, CURLINFO info, const char **out);

/// Otwiera parę pseudo-terminala; zwraca deskryptor mastera i ścieżkę strony slave (testy).
int wp_open_pty(char *slave_name, int len);

#endif
