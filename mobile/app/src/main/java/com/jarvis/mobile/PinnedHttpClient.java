package com.jarvis.mobile;

import android.annotation.SuppressLint;
import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;

import javax.net.ssl.HttpsURLConnection;
import javax.net.ssl.SSLContext;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

final class PinnedHttpClient {
    private PinnedHttpClient() { }

    @SuppressLint("CustomX509TrustManager")
    static JSONObject post(String endpoint, String route, String fingerprint,
                           String bearer, JSONObject body) throws Exception {
        String expected = ConnectionStore.normalize(fingerprint);
        X509TrustManager trust = new X509TrustManager() {
            @Override public void checkClientTrusted(X509Certificate[] chain, String authType)
                    throws CertificateException {
                throw new CertificateException("Jarvis Mobile does not accept client certificates");
            }
            @Override public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
            @Override public void checkServerTrusted(X509Certificate[] chain, String authType)
                    throws CertificateException {
                if (chain == null || chain.length == 0) throw new CertificateException("empty chain");
                try {
                    String actual = hex(MessageDigest.getInstance("SHA-256").digest(chain[0].getEncoded()));
                    if (!MessageDigest.isEqual(actual.getBytes(StandardCharsets.US_ASCII),
                            expected.getBytes(StandardCharsets.US_ASCII)))
                        throw new CertificateException("Jarvis certificate fingerprint changed");
                } catch (CertificateException exception) { throw exception; }
                catch (Exception exception) { throw new CertificateException(exception); }
            }
        };
        SSLContext ssl = SSLContext.getInstance("TLS");
        ssl.init(null, new TrustManager[]{trust}, new SecureRandom());
        HttpsURLConnection connection = (HttpsURLConnection) new URL(endpoint + route).openConnection();
        connection.setSSLSocketFactory(ssl.getSocketFactory());
        connection.setHostnameVerifier((host, session) -> true); // Identity is the pinned certificate.
        connection.setConnectTimeout(3000);
        connection.setReadTimeout(5000);
        connection.setRequestMethod("POST");
        connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
        if (bearer != null) connection.setRequestProperty("Authorization", "Bearer " + bearer);
        connection.setDoOutput(true);
        try (OutputStream output = connection.getOutputStream()) {
            output.write(body.toString().getBytes(StandardCharsets.UTF_8));
        }
        int status = connection.getResponseCode();
        InputStream stream = status >= 200 && status < 300
                ? connection.getInputStream() : connection.getErrorStream();
        StringBuilder text = new StringBuilder();
        if (stream != null) try (BufferedReader reader = new BufferedReader(
                new InputStreamReader(stream, StandardCharsets.UTF_8))) {
            String line;
            while ((line = reader.readLine()) != null) text.append(line);
        }
        connection.disconnect();
        if (status < 200 || status >= 300)
            throw new HttpFailure(status, text.toString());
        return new JSONObject(text.toString());
    }

    private static String hex(byte[] bytes) {
        StringBuilder value = new StringBuilder(bytes.length * 2);
        for (byte current : bytes) value.append(String.format("%02X", current));
        return value.toString();
    }

    static final class HttpFailure extends Exception {
        final int status;
        HttpFailure(int status, String message) { super(message); this.status = status; }
    }
}
