package org.shou.launcher.bootstrap;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.OutputStream;
import java.io.PrintStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Paths;
import java.nio.file.StandardOpenOption;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.List;

/** Independent bounded output: closing the launcher must not break or block a running game. */
final class SessionLog {
    private SessionLog() { }
    static void install(String[] arguments) throws IOException {
        String path = System.getProperty("shou.launcher.sessionLog");
        if (path == null) return; // Protocol/unit fixtures do not request detached logging.
        if (!Paths.get(path).isAbsolute()) throw new IOException("Session log path must be absolute.");
        final List<String> privateValues = new ArrayList<String>();
        for (int i = 0; i + 1 < arguments.length; i++) {
            String key = arguments[i].toLowerCase(java.util.Locale.ROOT);
            if (key.equals("--username") || key.equals("--uuid") || key.equals("--accesstoken")
                    || key.equals("--clientid") || key.equals("--xuid") || key.equals("--session") || key.equals("--userproperties")) {
                String value = arguments[i + 1];
                if (value.length() > 1) privateValues.add(value);
            }
        }
        Collections.sort(privateValues, new Comparator<String>() {
            public int compare(String a, String b) { return Integer.compare(b.length(), a.length()); }
        });
        final OutputStream file = Files.newOutputStream(Paths.get(path), StandardOpenOption.CREATE_NEW, StandardOpenOption.WRITE);
        final Sink sink = new Sink(file, privateValues);
        System.setOut(new PrintStream(new Lines(sink), true, "UTF-8"));
        System.setErr(new PrintStream(new Lines(sink), true, "UTF-8"));
        Runtime.getRuntime().addShutdownHook(new Thread(new Runnable() {
            public void run() { System.out.close(); System.err.close(); try { file.close(); } catch (IOException ignored) { } }
        }, "shou-session-log"));
    }
    private static final class Sink {
        final OutputStream file;
        final List<String> privateValues;
        long written;
        Sink(OutputStream file, List<String> values) { this.file = file; this.privateValues = values; }
        synchronized void line(byte[] bytes) throws IOException {
            if (written >= 8 * 1024 * 1024) return;
            String text = new String(bytes, StandardCharsets.UTF_8);
            for (String value : privateValues) text = text.replace(value, "<redacted>");
            text = text.replaceAll("(?i)Bearer\\s+[^\\s]+", "Bearer <redacted>")
                .replaceAll("(?i)(access[_-]?token|refresh[_-]?token|client[_-]?token|password|authorization)(\\s*[:=]\\s*|\\s+)([^\\s,;]+)", "$1$2<redacted>");
            byte[] safe = (text + "\n").getBytes(StandardCharsets.UTF_8);
            if (written + safe.length > 8 * 1024 * 1024) { written = 8 * 1024 * 1024; return; }
            file.write(safe); file.flush(); written += safe.length;
        }
    }
    private static final class Lines extends OutputStream {
        final Sink sink;
        final ByteArrayOutputStream pending = new ByteArrayOutputStream();
        boolean overlong;
        Lines(Sink sink) { this.sink = sink; }
        public synchronized void write(int value) throws IOException {
            if (value == '\n') {
                sink.line(overlong ? "[overlong line omitted]".getBytes(StandardCharsets.UTF_8) : pending.toByteArray());
                pending.reset(); overlong = false;
            } else if (!overlong) {
                if (pending.size() >= 32768) { pending.reset(); overlong = true; }
                else pending.write(value);
            }
        }
        public synchronized void close() throws IOException {
            if (pending.size() > 0 || overlong) write('\n');
        }
    }
}
