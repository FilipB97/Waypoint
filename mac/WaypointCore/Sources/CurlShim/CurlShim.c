#include "CurlShim.h"

CURLcode wp_set_long(CURL *h, CURLoption opt, long v) { return curl_easy_setopt(h, opt, v); }
CURLcode wp_set_off(CURL *h, CURLoption opt, curl_off_t v) { return curl_easy_setopt(h, opt, v); }
CURLcode wp_set_str(CURL *h, CURLoption opt, const char *v) { return curl_easy_setopt(h, opt, v); }
CURLcode wp_set_ptr(CURL *h, CURLoption opt, void *v) { return curl_easy_setopt(h, opt, v); }
CURLcode wp_set_list(CURL *h, CURLoption opt, struct curl_slist *v) { return curl_easy_setopt(h, opt, v); }

CURLcode wp_set_write(CURL *h, wp_data_cb cb, void *userdata) {
    CURLcode r = curl_easy_setopt(h, CURLOPT_WRITEFUNCTION, cb);
    return r != CURLE_OK ? r : curl_easy_setopt(h, CURLOPT_WRITEDATA, userdata);
}

CURLcode wp_set_read(CURL *h, wp_data_cb cb, void *userdata) {
    CURLcode r = curl_easy_setopt(h, CURLOPT_READFUNCTION, cb);
    return r != CURLE_OK ? r : curl_easy_setopt(h, CURLOPT_READDATA, userdata);
}

CURLcode wp_set_progress(CURL *h, wp_progress_cb cb, void *userdata) {
    CURLcode r = curl_easy_setopt(h, CURLOPT_XFERINFOFUNCTION, cb);
    if (r != CURLE_OK) return r;
    r = curl_easy_setopt(h, CURLOPT_XFERINFODATA, userdata);
    return r != CURLE_OK ? r : curl_easy_setopt(h, CURLOPT_NOPROGRESS, 0L);
}

CURLcode wp_get_long(CURL *h, CURLINFO info, long *out) { return curl_easy_getinfo(h, info, out); }
CURLcode wp_get_off(CURL *h, CURLINFO info, curl_off_t *out) { return curl_easy_getinfo(h, info, out); }
CURLcode wp_get_str(CURL *h, CURLINFO info, const char **out) { return curl_easy_getinfo(h, info, out); }
