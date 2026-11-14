package org.shou.launcher.bootstrap;

import java.io.ByteArrayInputStream;
import java.io.DataInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.nio.ByteBuffer;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;

/**
 * Draft trusted entry point. Compile with javac --release 8, package/hash through
 * the owner-controlled product build, and invoke without application arguments.
 * Credentials arrive through inherited stdin, never the Java launcher's argv.
 */
public final class BootstrapMain {
    private static final byte[] MAGIC = { 'S', 'H', 'O', 'U', 'B', 'O', 'O', 'T' };
    private static final int VERSION = 1;
    private static final int MAX_BODY_BYTES = 1024 * 1024;
    private static final int MAX_CLASS_BYTES = 4096;
    private static final int MAX_ARGUMENT_BYTES = 256 * 1024;
    private static final int MAX_ARGUMENTS = 65536;

    private BootstrapMain() { }

    public static void main(String[] publicArguments) {
        final LaunchInput launch;
        try {
            if (publicArguments.length != 0) {
                throw new IOException("Unexpected public application arguments.");
            }
            // The parent writes exactly one frame and closes the pipe. The helper owns no disk payload.
            try (InputStream source = System.in) {
                launch = readInput(source);
            }
        } catch (Throwable failure) {
            fail("SHOU bootstrap: invalid or incomplete input.", 64);
            return;
        }

        try {
            SessionLog.install(launch.arguments);
            ClassLoader loader = Thread.currentThread().getContextClassLoader();
            Class<?> target = Class.forName(launch.mainClass, true, loader);
            Method entry = target.getMethod("main", String[].class);
            if (!Modifier.isStatic(entry.getModifiers()) || entry.getReturnType() != Void.TYPE) {
                throw new NoSuchMethodException("Target requires public static void main(String[]).");
            }
            // Do not create another thread: macOS -XstartOnFirstThread must retain its original main thread.
            entry.invoke(null, new Object[] { launch.arguments });
        } catch (InvocationTargetException failure) {
            // Target exception messages/stacks may contain its arguments. Launcher logs redact separately.
            fail("SHOU bootstrap: game entry failed.", 70);
        } catch (Throwable failure) {
            fail("SHOU bootstrap: game entry is unavailable.", 70);
        }
    }

    private static LaunchInput readInput(InputStream source) throws IOException {
        DataInputStream input = new DataInputStream(source);
        byte[] magic = new byte[MAGIC.length];
        input.readFully(magic);
        if (!Arrays.equals(MAGIC, magic) || input.readUnsignedShort() != VERSION) {
            throw new IOException("Unsupported input protocol.");
        }
        int bodyLength = input.readInt();
        if (bodyLength < 8 || bodyLength > MAX_BODY_BYTES) {
            throw new IOException("Input exceeds protocol bounds.");
        }
        byte[] body = new byte[bodyLength];
        try {
            input.readFully(body);
            // EOF is part of the protocol. No appended frame, padding or unexplained bytes are accepted.
            if (input.read() != -1) {
                throw new IOException("Unexpected trailing input.");
            }
            DataInputStream fields = new DataInputStream(new ByteArrayInputStream(body));
            String mainClass = readString(fields, MAX_CLASS_BYTES, false);
            int count = fields.readInt();
            if (count < 0 || count > MAX_ARGUMENTS || count > fields.available() / 4) {
                throw new IOException("Invalid argument count.");
            }
            String[] arguments = new String[count];
            for (int index = 0; index < count; index++) {
                arguments[index] = readString(fields, MAX_ARGUMENT_BYTES, true);
            }
            if (fields.available() != 0) {
                throw new IOException("Unexpected payload fields.");
            }
            return new LaunchInput(mainClass, arguments);
        } finally {
            Arrays.fill(body, (byte) 0);
        }
    }

    private static String readString(DataInputStream input, int maximum, boolean allowEmpty) throws IOException {
        int length = input.readInt();
        if (length < (allowEmpty ? 0 : 1) || length > maximum || length > input.available()) {
            throw new IOException("Invalid UTF-8 field length.");
        }
        byte[] encoded = new byte[length];
        try {
            input.readFully(encoded);
            String value;
            try {
                value = StandardCharsets.UTF_8.newDecoder()
                    .onMalformedInput(CodingErrorAction.REPORT)
                    .onUnmappableCharacter(CodingErrorAction.REPORT)
                    .decode(ByteBuffer.wrap(encoded)).toString();
            } catch (CharacterCodingException invalid) {
                throw new IOException("Invalid UTF-8 input.");
            }
            if (value.indexOf('\0') >= 0) {
                throw new IOException("NUL is not a supported argument character.");
            }
            return value;
        } finally {
            Arrays.fill(encoded, (byte) 0);
        }
    }

    private static void fail(String message, int exitCode) {
        System.err.println(message);
        System.exit(exitCode);
    }

    private static final class LaunchInput {
        final String mainClass;
        final String[] arguments;
        LaunchInput(String target, String[] values) { mainClass = target; arguments = values; }
    }
}
