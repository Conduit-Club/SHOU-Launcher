package org.shou.launcher.bootstrap;

import java.io.*;
import java.lang.reflect.*;
import java.net.*;
import java.nio.file.*;
import java.security.*;
import java.util.*;

/** Headless, install-only adapter. Never invokes a vendor GUI, launcher, game, or process killer. */
public final class InstallerBridge {
    private static Object call(Object target, String method, Class<?>[] types, Object... args) throws Exception {
        return target.getClass().getMethod(method, types).invoke(target, args);
    }
    private static Object get(Object target, String method) throws Exception { return call(target, method, new Class<?>[0]); }
    public static void main(String[] args) throws Exception {
        if (args.length != 6 || !Boolean.getBoolean("java.awt.headless")) throw new IllegalArgumentException("Install-only arguments required");
        final Path root = Paths.get(args[1]).toRealPath();
        if (!root.equals(Paths.get(System.getProperty("user.dir")).toRealPath())) throw new IOException("Installer root mismatch");
        final PrintStream completion=System.out;
        System.setOut(new PrintStream(new OutputStream(){public void write(int value){} public void write(byte[] bytes,int offset,int length){}}));
        URL.setURLStreamHandlerFactory(protocol -> "http".equals(protocol) ? new URLStreamHandler() {
            protected URLConnection openConnection(URL url) throws IOException { throw new IOException("Plain HTTP installer transport is disabled"); }
        } : null);
        if ("optifine".equals(args[0])) {
            Class<?> installer = Class.forName("optifine.Installer");
            String version = (String)installer.getMethod("getOptiFineVersion").invoke(null);
            if (!version.endsWith(args[2] + "_" + args[3])) throw new IOException("OptiFine version mismatch");
            installer.getMethod("doInstall", File.class).invoke(null, root.toFile());
        } else if ("labymod".equals(args[0])) {
            // VanillaInstaller.install closes the user's official launcher. Use data/file APIs only.
            Object api = Class.forName("net.laby.installer.api.LabyModApi").getConstructor().newInstance();
            Object manifest = call(api, "getManifest", new Class<?>[]{String.class}, args[5]);
            if (!args[4].equals(get(manifest, "getCommitReference"))) throw new IOException("LabyMod release changed; refresh before retrying");
            if (!args[3].equals(get(manifest, "getLabyModVersion"))) throw new IOException("LabyMod version mismatch");
            Object target = null;
            Object metadata = get(manifest, "getMinecraftVersions");
            for (int i=0; i<Array.getLength(metadata); i++) {
                Object row=Array.get(metadata,i);
                if (args[2].equals(get(row,"getVersion"))) { if(target!=null) throw new IOException("Ambiguous LabyMod target"); target=row; }
            }
            if (target==null) throw new IOException("LabyMod does not support this Minecraft version");
            Object libraries = call(api, "getLibraries", new Class<?>[]{String.class}, args[5]);
            Path mainJar=root.resolve("libraries/net/labymod/LabyMod/4/LabyMod-4.jar");
            Files.createDirectories(mainJar.getParent());
            Class<?> callbackType=Class.forName("net.laby.installer.util.IoUtil$ProgressCallback");
            Object callback=java.lang.reflect.Proxy.newProxyInstance(callbackType.getClassLoader(),new Class<?>[]{callbackType},(p,m,a)->null);
            call(api,"downloadLabyMod",new Class<?>[]{String.class,String.class,Path.class,callbackType},args[5],args[4],mainJar,callback);
            long size=((Number)get(manifest,"getSize")).longValue();
            String expected=(String)get(manifest,"getSha1");
            long actualSize=Files.size(mainJar);
            if(size<=0||size>128L*1024*1024||actualSize<=0||actualSize>128L*1024*1024||expected==null||!expected.matches("[a-fA-F0-9]{40}")) throw new IOException("LabyMod binary identity is invalid");
            MessageDigest digest=MessageDigest.getInstance("SHA-1");
            try(InputStream input=Files.newInputStream(mainJar)){byte[] buffer=new byte[65536];int n;while((n=input.read(buffer))!=-1)digest.update(buffer,0,n);}
            StringBuilder hex=new StringBuilder();for(byte b:digest.digest())hex.append(String.format(java.util.Locale.ROOT,"%02x",b&255));
            if(!expected.equalsIgnoreCase(hex.toString()))throw new IOException("LabyMod binary checksum mismatch");
            // The published digest authenticates the exact file. The upstream size is advisory;
            // canonicalize it only after matching that digest, and retain strict pack size checks.
            size=actualSize;
            Object writer = Class.forName("net.laby.installer.install.vanilla.ManifestWriter").getConstructors()[0].newInstance(api);
            String profile = "shou-labymod-" + args[2] + "-" + args[5] + "-" + args[3];
            String custom=(String)get(target,"getCustomManifestUrl");
            if(custom!=null && !custom.startsWith("https://")) throw new IOException("LabyMod manifest must use HTTPS");
            String method = custom==null ? "writeLegacyManifest" : "writeModernManifest";
            Class<?>[] signature = {Path.class,String.class,target.getClass(),libraries.getClass(),String.class,String.class,String.class,String.class,long.class};
            writer.getClass().getMethod(method,signature).invoke(writer,root,profile,target,libraries,args[3],args[4],args[5],get(manifest,"getSha1"),size);
            Object assets=get(manifest,"getAssets");
            if(assets instanceof Map) {
                for(Object key:((Map<?,?>)assets).keySet()) {
                    String s=String.valueOf(key);
                    if(s.startsWith("/")||s.contains("..")||s.contains("\\")||s.contains(":")) throw new IOException("Unsafe LabyMod asset key");
                }
                Class<?> setup=Class.forName("net.laby.installer.install.LabyModFileSetup");
                setup.getMethod("downloadAssets",api.getClass(),String.class,String.class,Map.class,Path.class)
                    .invoke(null,api,args[5],args[4],assets,root.resolve("labymod-neo/assets"));
            }
            Class<?> setup=Class.forName("net.laby.installer.install.LabyModFileSetup");
            Object isolated=setup.getMethod("readIsolatedLibraries",Path.class).invoke(null,mainJar);
            Class<?> platform=Class.forName("net.laby.installer.platform.Platform");
            Object filtered=setup.getMethod("filterIsolatedLibraries",List.class,String.class,platform).invoke(null,isolated,get(target,"getVersion"),platform.getMethod("detect").invoke(null));
            setup.getMethod("downloadIsolatedLibraries",api.getClass(),List.class,Path.class,callbackType).invoke(null,api,filtered,root.resolve("labymod-neo/libraries"),callback);
        } else throw new IllegalArgumentException("Unsupported install adapter");
        completion.println("SHOU_INSTALLER_COMPLETE");
    }
}
