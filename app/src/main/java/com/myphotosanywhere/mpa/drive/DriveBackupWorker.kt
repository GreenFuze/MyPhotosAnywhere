package com.myphotosanywhere.mpa.drive

import android.content.Context
import androidx.work.CoroutineWorker
import androidx.work.WorkerParameters
import com.myphotosanywhere.mpa.MpaApplication
import java.io.File
import java.io.IOException
import java.io.RandomAccessFile
import java.security.MessageDigest
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext

class DriveBackupWorker(context: Context, parameters: WorkerParameters) : CoroutineWorker(context, parameters) {
    override suspend fun doWork(): Result = withContext(Dispatchers.IO) {
        val app = applicationContext as MpaApplication
        val dao = app.database.drive()
        val id = inputData.getString("backupId") ?: return@withContext Result.failure()
        var task = dao.backup(id) ?: return@withContext Result.success()
        if (task.state == "COMPLETE") return@withContext Result.success()
        val auth = DriveAuthorization(applicationContext)
        var token: String? = null
        suspend fun stillActive(): Boolean {
            val active = dao.connection() ?: return false
            return active.backupEnabled && active.binding()?.key == task.bindingKey &&
                DriveBinding(task.accountId, task.folderId).accepts(active.accountId, active.folderId ?: "")
        }
        try {
            if (!stillActive()) return@withContext Result.success()
            val photo = app.database.photos().find(task.photoId) ?: error("The local photo is missing.")
            val original = File(photo.originalPath)
            check(original.isFile && original.length() == photo.bytes) { "The local original is missing or changed." }
            token = auth.token(task.email, DriveMode.valueOf(task.mode))
            val api = DriveGateway(token)
            val user = api.user()
            if (user.id != task.accountId || !user.email.equals(task.email, ignoreCase = true)) throw DriveAuthorizationNeeded()
            val folder = api.file(task.folderId)
            check(folder.mimeType == DriveGateway.FOLDER_MIME && folder.canAddChildren) { "The selected Drive folder is not writable." }
            val md5 = digest(original, "MD5")
            check(digest(original, "SHA-256") == photo.sha256) { "The local original has changed. Backup stopped." }
            if (task.remoteId == null) {
                task = task.copy(remoteId = api.generateId(), state = "UPLOADING", errorMessage = null)
                dao.updateBackup(task) // Persist the reserved ID before any create request; retries reuse it.
            }
            val remoteId = requireNotNull(task.remoteId)
            val existing = try { api.file(remoteId) } catch (error: DriveHttpException) {
                if (error.status == 404) null else throw error
            }
            if (existing != null) {
                check(existing.md5 == md5 && existing.bytes == photo.bytes && task.folderId in existing.parents) {
                    "The reserved Drive file differs from this photo. Backup stopped without overwriting it."
                }
            } else {
                var session = task.sessionUri
                var progress = if (session != null) try { api.probe(session, photo.bytes) }
                    catch (error: DriveHttpException) {
                        if (error.status == 404 || error.status == 410) { session = null; UploadProgress(0, false) } else throw error
                    } else UploadProgress(0, false)
                if (session == null) {
                    session = api.beginUpload(remoteId, photo.displayName, photo.mimeType, photo.bytes, task.folderId, photo.id, photo.parentId)
                    task = task.copy(sessionUri = session, state = "UPLOADING", errorMessage = null)
                    dao.updateBackup(task)
                }
                RandomAccessFile(original, "r").use { input ->
                    while (!progress.complete && progress.offset < photo.bytes) {
                        currentCoroutineContext().ensureActive()
                        if (!stillActive()) return@withContext Result.success()
                        val start = progress.offset
                        val data = ByteArray(minOf(CHUNK_BYTES.toLong(), photo.bytes - start).toInt())
                        input.seek(start); input.readFully(data)
                        progress = api.chunk(requireNotNull(session), photo.bytes, start, data)
                        check(progress.complete || progress.offset > start) { "Drive did not acknowledge this upload chunk." }
                        task = task.copy(uploadedBytes = progress.offset, state = "UPLOADING", errorMessage = null)
                        dao.updateBackup(task)
                    }
                }
            }
            val uploaded = api.file(remoteId)
            check(uploaded.bytes == photo.bytes && uploaded.md5 == md5 && task.folderId in uploaded.parents) {
                "Drive checksum verification failed. The local original is preserved."
            }
            dao.updateBackup(task.copy(state = "COMPLETE", uploadedBytes = photo.bytes, sessionUri = null,
                remoteMd5 = md5, remoteVersion = uploaded.version, errorMessage = null))
            Result.success()
        } catch (cancelled: CancellationException) { throw cancelled }
        catch (needed: DriveAuthorizationNeeded) {
            dao.updateBackup(task.copy(state = "AUTH_REQUIRED", errorMessage = needed.message))
            Result.success() // UI reconnect schedules a fresh task; never launch consent from the background.
        } catch (error: DriveHttpException) {
            if (error.status == 401) {
                token?.let { runCatching { auth.invalidate(it) } }
                dao.updateBackup(task.copy(state = "AUTH_REQUIRED", errorMessage = error.message)); Result.success()
            } else {
                val retry = error.retryable && runAttemptCount < 5
                dao.updateBackup(task.copy(state = if (retry) "RETRYING" else "FAILED", errorMessage = error.message))
                if (retry) Result.retry() else Result.failure()
            }
        } catch (error: IOException) {
            val retry = runAttemptCount < 5
            dao.updateBackup(task.copy(state = if (retry) "RETRYING" else "FAILED",
                errorMessage = "Network interrupted. Your local original is preserved."))
            if (retry) Result.retry() else Result.failure()
        } catch (error: Exception) {
            dao.updateBackup(task.copy(state = "FAILED", errorMessage = error.message ?: "Backup failed. Original preserved."))
            Result.failure()
        }
    }

    companion object {
        private const val CHUNK_BYTES = 1024 * 1024
        fun digest(file: File, algorithm: String): String {
            val digest = MessageDigest.getInstance(algorithm)
            file.inputStream().use { input ->
                val buffer = ByteArray(64 * 1024)
                while (true) { val n = input.read(buffer); if (n < 0) break; digest.update(buffer, 0, n) }
            }
            return digest.digest().joinToString("") { "%02x".format(it) }
        }
    }
}
