package com.myphotosanywhere.mpa;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.content.Context;
import android.content.Intent;
import android.database.Cursor;
import android.net.Uri;
import android.os.Binder;
import android.os.Bundle;
import android.os.ParcelFileDescriptor;
import java.io.File;
import java.io.FileNotFoundException;
import java.io.FileOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.zip.ZipEntry;
import java.util.zip.ZipOutputStream;

/** Isolated test APK provider. Use SDK/Java only: target-app libraries are absent in this process. */
public final class TakeoutTestProvider extends ContentProvider {
    private File archive() { return new File(getContext().getCacheDir(), "private-takeout.zip"); }
    @Override public boolean onCreate() { return true; }
    @Override public Bundle call(String method, String arg, Bundle extras) {
        Context ctx = getContext();
        String[] callers = ctx.getPackageManager().getPackagesForUid(Binder.getCallingUid());
        if (callers == null || !Arrays.asList(callers).contains("com.myphotosanywhere.mpa"))
            throw new SecurityException("Only the app under test may prepare fixtures");
        Uri uri = Uri.parse("content://com.myphotosanywhere.mpa.test.takeout/archive");
        if ("prepare".equals(method)) {
            try (ZipOutputStream zip = new ZipOutputStream(new FileOutputStream(archive()))) {
                zip.putNextEntry(new ZipEntry("Album/photo.jpg"));
                zip.write(new byte[]{1,2,3,4}); zip.closeEntry();
                zip.putNextEntry(new ZipEntry("Album/photo.jpg.json"));
                zip.write(("{\"title\":\"photo.jpg\",\"description\":\"Granted document\",\"photoTakenTime\":{\"timestamp\":\"123\"}}").getBytes(StandardCharsets.UTF_8));
                zip.closeEntry();
            } catch (IOException e) { throw new IllegalStateException("Cannot prepare fixture", e); }
            long identity = Binder.clearCallingIdentity();
            try {
                ctx.grantUriPermission("com.myphotosanywhere.mpa", uri,
                    Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION);
            } finally { Binder.restoreCallingIdentity(identity); }
            Bundle result = new Bundle(); result.putString("path", archive().getAbsolutePath()); return result;
        }
        if (!"cleanup".equals(method)) throw new IllegalArgumentException("Unknown fixture operation");
        long identity = Binder.clearCallingIdentity();
        try { ctx.revokeUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION); }
        finally { Binder.restoreCallingIdentity(identity); }
        if (archive().exists() && !archive().delete()) throw new IllegalStateException("Cannot clean fixture");
        return new Bundle();
    }
    @Override public ParcelFileDescriptor openFile(Uri uri, String mode) throws FileNotFoundException {
        if (!"r".equals(mode)) throw new SecurityException("Read-only fixture");
        if ("archive".equals(uri.getLastPathSegment()))
            return ParcelFileDescriptor.open(archive(), ParcelFileDescriptor.MODE_READ_ONLY);
        if ("pipe".equals(uri.getLastPathSegment())) {
            try {
                ParcelFileDescriptor[] pipe = ParcelFileDescriptor.createPipe();
                pipe[1].close(); return pipe[0];
            } catch (IOException e) { throw new FileNotFoundException(e.getMessage()); }
        }
        throw new SecurityException("Read access denied by test provider");
    }
    @Override public String getType(Uri uri) { return "application/zip"; }
    @Override public Cursor query(Uri uri, String[] projection, String selection, String[] args, String sort) { return null; }
    @Override public Uri insert(Uri uri, ContentValues values) { throw new UnsupportedOperationException(); }
    @Override public int delete(Uri uri, String selection, String[] args) { throw new UnsupportedOperationException(); }
    @Override public int update(Uri uri, ContentValues values, String selection, String[] args) { throw new UnsupportedOperationException(); }
}
