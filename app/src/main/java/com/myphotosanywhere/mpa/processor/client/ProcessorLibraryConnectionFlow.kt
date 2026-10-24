package com.myphotosanywhere.mpa.processor.client

import android.app.PendingIntent
import android.content.Intent
import android.database.sqlite.SQLiteCantOpenDatabaseException
import android.database.sqlite.SQLiteDatabaseCorruptException
import android.database.sqlite.SQLiteDiskIOException
import android.database.sqlite.SQLiteFullException
import androidx.lifecycle.SavedStateHandle
import com.google.android.gms.common.api.ApiException
import com.myphotosanywhere.mpa.drive.GoogleAuthorizationCanceled
import com.myphotosanywhere.mpa.drive.GoogleAuthorizationLaunchFailed
import com.myphotosanywhere.mpa.drive.GoogleAuthorizationMissingResult
import com.myphotosanywhere.mpa.drive.GoogleAuthorizationReturn
import com.myphotosanywhere.mpa.processor.protocol.LibraryConnectionDescriptor
import com.myphotosanywhere.mpa.processor.protocol.ProcessorProtocolException
import java.io.IOException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

internal enum class ProcessorLibraryConnectionPhase { LOADING, IDLE, AUTHORIZING, GOOGLE_CONSENT, CHECKING, REVIEW, APPROVING, BLOCKED }
internal data class ProcessorLibraryConnectionState(
    val phase: ProcessorLibraryConnectionPhase = ProcessorLibraryConnectionPhase.LOADING,
    val draft: String = "",
    val accepted: ProcessorLibraryRegistration? = null,
    val review: ProcessorLibraryConnectionReview? = null,
    val acknowledged: Boolean = false,
    val message: String? = null,
) {
    val busy get() = phase in listOf(ProcessorLibraryConnectionPhase.LOADING,ProcessorLibraryConnectionPhase.AUTHORIZING,ProcessorLibraryConnectionPhase.CHECKING,ProcessorLibraryConnectionPhase.APPROVING)
    val editable get() = !busy && phase != ProcessorLibraryConnectionPhase.GOOGLE_CONSENT && phase != ProcessorLibraryConnectionPhase.BLOCKED
}

/** Presentation owner only. SavedState contains bounded draft/public code and selected
 * email, never a token or human approval. Native I/O belongs to each joined access call.
 * Unknown programming errors propagate; damaged accepted state blocks this client. */
internal class ProcessorLibraryConnectionFlow(private val saved: SavedStateHandle, private val scope: CoroutineScope,
    private val access: ProcessorLibraryConnectionAccess) {
    private val mutable = MutableStateFlow(ProcessorLibraryConnectionState(draft = saved.get<String>(DRAFT)?.takeIf { it.length <= LibraryConnectionDescriptor.MAX_CODE_CHARACTERS } ?: ""))
    val state = mutable.asStateFlow()
    private var active: Job? = null
    private var ownerSequence = 0L
    private class ConsentTarget(val identity: Any,val launch: (PendingIntent)->Unit)
    private var consentTarget: ConsentTarget? = null
    private var pendingConsent: PendingIntent? = null
    private val initialization: Job
    init {
        initialization = scope.launch {
            handle(0,ProcessorLibraryConnectionPhase.LOADING) {
                loadCurrent()
            }
        }
        active = initialization
    }
    private suspend fun loadCurrent() {
        val previous = mutable.value.accepted
        val current = access.current()
        if (previous != null && (current == null || current.descriptor.encode() != previous.descriptor.encode()))
            throw ProcessorLibraryStateCorrupt("Previously accepted library trust changed during its owned read.")
        mutable.value = mutable.value.copy(phase=ProcessorLibraryConnectionPhase.IDLE,accepted=current,
            draft=mutable.value.draft.ifEmpty { current?.descriptor?.encode().orEmpty() })
    }
    fun refresh() {
        if (mutable.value.editable) command(ProcessorLibraryConnectionPhase.LOADING) { loadCurrent() }
    }
    fun draft(value: String) {
        if (!mutable.value.editable) return
        if (value.length > LibraryConnectionDescriptor.MAX_CODE_CHARACTERS) { report("This connection code is too long. Copy it again from MPA on your computer."); return }
        saved[DRAFT] = value; clearPending()
        mutable.value = mutable.value.copy(draft=value,review=null,acknowledged=false,phase=ProcessorLibraryConnectionPhase.IDLE,message=null)
    }
    fun acknowledge(value: Boolean) {
        if (mutable.value.phase == ProcessorLibraryConnectionPhase.REVIEW) mutable.value = mutable.value.copy(acknowledged=value)
    }
    fun report(message: String) { mutable.value = mutable.value.copy(message=message) }
    fun attachConsentLauncher(launch: (PendingIntent)->Unit): Any {
        val identity = Any()
        consentTarget = ConsentTarget(identity,launch)
        return identity
    }
    fun activateConsentLauncher(identity: Any) {
        if (consentTarget?.identity !== identity) return
        try { dispatchConsent() }
        catch (failure: Throwable) { detachConsentLauncher(identity); throw failure }
    }
    fun detachConsentLauncher(identity: Any) {
        if (consentTarget?.identity === identity) consentTarget = null
    }
    private fun dispatchConsent() {
        if (mutable.value.phase != ProcessorLibraryConnectionPhase.GOOGLE_CONSENT) return
        val pending = pendingConsent ?: return
        val target = consentTarget ?: return
        pendingConsent = null
        target.launch(pending)
    }
    fun authorize(email: String) {
        if (!mutable.value.editable) return
        val code = mutable.value.draft
        mutable.value = mutable.value.copy(review=null,acknowledged=false)
        command(ProcessorLibraryConnectionPhase.AUTHORIZING) {
            withContext(Dispatchers.Default) { LibraryConnectionDescriptor.decode(code) }
            // Bound local selectors before they can enter SavedState or Google SDK calls.
            ProcessorLibraryAccount.validateEmail(email)
            saved[PENDING_CODE] = code; saved[PENDING_EMAIL] = email
            when (val result = access.authorize(email)) {
                is ProcessorLibraryAuthorization.Granted -> inspect(code,email,result)
                is ProcessorLibraryAuthorization.Consent -> {
                    pendingConsent = result.pending
                    mutable.value = mutable.value.copy(phase=ProcessorLibraryConnectionPhase.GOOGLE_CONSENT)
                    dispatchConsent()
                }
            }
        }
    }
    fun returned(resultCode: Int, intent: Intent?) {
        // ActivityResult may arrive during recreation's initial native read. Retain the
        // callback under the same parent scope and join initialization before admission.
        scope.launch {
            initialization.join()
            val code = saved.get<String>(PENDING_CODE); val email = saved.get<String>(PENDING_EMAIL)
            if (mutable.value.phase == ProcessorLibraryConnectionPhase.BLOCKED) return@launch
            if (code == null || email == null || code != mutable.value.draft || mutable.value.busy) {
                report("This Google sign-in no longer matches the current connection. Choose your account again."); return@launch
            }
            command(ProcessorLibraryConnectionPhase.CHECKING) {
                ProcessorLibraryAccount.validateEmail(email)
                inspect(code,email,access.returned(resultCode,intent))
            }
        }
    }
    private suspend fun inspect(code: String, email: String, grant: ProcessorLibraryAuthorization.Granted) {
        mutable.value = mutable.value.copy(phase=ProcessorLibraryConnectionPhase.CHECKING)
        val review = access.review(code,email,grant)
        check(review.descriptor.encode() == code) { "Connection reader substituted the reviewed library." }
        clearPending()
        mutable.value = mutable.value.copy(phase=ProcessorLibraryConnectionPhase.REVIEW,review=review,acknowledged=false,message=null)
    }
    fun approve() {
        val snapshot = mutable.value; val review = snapshot.review
        if (snapshot.phase != ProcessorLibraryConnectionPhase.REVIEW || !snapshot.acknowledged || review == null) return
        command(ProcessorLibraryConnectionPhase.APPROVING) {
            val accepted = access.approve(review,review.descriptor.documentSha256)
            check(accepted.descriptor.encode() == review.descriptor.encode() && accepted.account == review.account) { "Connection approval acknowledged different accepted state." }
            saved[DRAFT] = accepted.descriptor.encode()
            mutable.value = mutable.value.copy(phase=ProcessorLibraryConnectionPhase.IDLE,accepted=accepted,review=null,acknowledged=false,message=null)
        }
    }
    fun cancelRead() {
        if (mutable.value.phase in listOf(ProcessorLibraryConnectionPhase.AUTHORIZING,ProcessorLibraryConnectionPhase.CHECKING)) active?.cancel()
    }
    fun consentLaunchFailed() { operational("Google sign-in could not open. Retry or update Google Play services.") }
    private fun command(phase: ProcessorLibraryConnectionPhase, action: suspend () -> Unit) {
        if (mutable.value.busy || mutable.value.phase == ProcessorLibraryConnectionPhase.BLOCKED) return
        val previous = active
        val owner = Math.addExact(ownerSequence,1).also { ownerSequence = it }
        mutable.value = mutable.value.copy(phase=phase,message=null)
        val task = scope.launch(start=CoroutineStart.LAZY) {
            handle(owner,phase) {
                try { previous?.join(); action() }
                finally { withContext(NonCancellable) { previous?.join() } }
            }
        }
        active = task
        task.start()
    }
    private suspend fun handle(owner: Long, phase: ProcessorLibraryConnectionPhase, action: suspend () -> Unit) {
        try { action() }
        catch (cancelled: CancellationException) { clearPending(); throw cancelled }
        catch (error: ProcessorLibraryStateCorrupt) { block() }
        catch (error: SQLiteDatabaseCorruptException) { block() }
        catch (error: SQLiteFullException) { operational(ProcessorClientStorageUnavailable().message.orEmpty()) }
        catch (error: SQLiteDiskIOException) { operational(ProcessorClientStorageUnavailable().message.orEmpty()) }
        catch (error: SQLiteCantOpenDatabaseException) { operational("Android could not open saved library state. Your accepted connection was preserved. Free some space and retry.") }
        catch (error: ProcessorProtocolException) { operational("This connection code is invalid or unsupported. Copy it again from MPA on your computer.") }
        catch (error: IOException) { operational(error.message ?: "The library connection could not finish. Your accepted state was preserved.") }
        catch (error: ApiException) { operational(GoogleAuthorizationReturn.message(error)) }
        catch (error: GoogleAuthorizationCanceled) { operational("Google sign-in was canceled. Choose your account again when ready.") }
        catch (error: GoogleAuthorizationLaunchFailed) { operational("Google sign-in could not open. Retry or update Google Play services.") }
        catch (error: GoogleAuthorizationMissingResult) { operational("Google returned no sign-in result. Choose your account again.") }
        finally {
            if (ownerSequence == owner && (mutable.value.phase == phase || mutable.value.busy))
                mutable.value = mutable.value.copy(phase=ProcessorLibraryConnectionPhase.IDLE)
        }
    }
    private fun operational(message: String) {
        clearPending(); mutable.value = mutable.value.copy(phase=if(mutable.value.review != null) ProcessorLibraryConnectionPhase.REVIEW else ProcessorLibraryConnectionPhase.IDLE,acknowledged=false,message=message)
    }
    private fun block() {
        clearPending(); mutable.value = mutable.value.copy(phase=ProcessorLibraryConnectionPhase.BLOCKED,review=null,acknowledged=false,
            message="Saved library verification failed. This connection has stopped; existing photos and accepted state were kept.")
    }
    private fun clearPending() { pendingConsent = null; saved.remove<String>(PENDING_CODE); saved.remove<String>(PENDING_EMAIL) }
    private companion object {
        const val DRAFT = "processor.connection.draft"
        const val PENDING_CODE = "processor.connection.pending.code"
        const val PENDING_EMAIL = "processor.connection.pending.email"
    }
}
