package com.myphotosanywhere.mpa.processor.client

import android.app.Activity
import android.app.Application
import android.app.PendingIntent
import android.content.Intent
import androidx.lifecycle.SavedStateHandle
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.LifecycleRegistry
import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.processor.protocol.LibraryConnectionDescriptor
import java.io.IOException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineExceptionHandler
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.job
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class ProcessorConnectionFlowTest {
    private fun descriptor(): LibraryConnectionDescriptor = checkNotNull(javaClass.classLoader?.getResourceAsStream("v1/library-connection.json"))
        .bufferedReader().use { LibraryConnectionDescriptor.decode(JsonParser.parseReader(it).asJsonObject["valid"].asJsonArray[0].asJsonObject["code"].asString) }
    private class Access(val descriptor: LibraryConnectionDescriptor) : ProcessorLibraryConnectionAccess {
        val account = ProcessorLibraryAccount(descriptor.workspace.accountId,"synthetic@example.invalid")
        val review = ProcessorLibraryConnectionReview(descriptor,ProcessorLibraryWorkspace(descriptor.workspace.folderId,"Metadata",7,true,false),account)
        var accepted: ProcessorLibraryRegistration? = null
        var currentGate: (suspend () -> Unit)? = null
        var authorizeGate: (suspend () -> Unit)? = null
        var approveGate: (suspend () -> Unit)? = null
        var reviewFailure: IOException? = null
        var approveFailure: IOException? = null
        var currentFailure: Exception? = null
        var authorization: ProcessorLibraryAuthorization = ProcessorLibraryAuthorization.Granted("synthetic-access")
        var authorizations = 0; var reviews = 0; var approvals = 0; var returns = 0
        override suspend fun current(): ProcessorLibraryRegistration? { currentGate?.invoke(); currentFailure?.let { throw it }; return accepted }
        override suspend fun authorize(email: String): ProcessorLibraryAuthorization {
            assertEquals(account.email,email); authorizations++; authorizeGate?.invoke(); currentCoroutineContext().ensureActive(); return authorization
        }
        override fun returned(resultCode: Int,intent: Intent?): ProcessorLibraryAuthorization.Granted { returns++; return ProcessorLibraryAuthorization.Granted("returned-access") }
        override suspend fun review(code: String,email: String,grant: ProcessorLibraryAuthorization.Granted): ProcessorLibraryConnectionReview {
            assertEquals(descriptor.encode(),code); assertEquals(account.email,email); reviews++; reviewFailure?.let { throw it }; return review
        }
        override suspend fun approve(review: ProcessorLibraryConnectionReview,approvedDigest: String): ProcessorLibraryRegistration {
            assertSame(this.review,review); assertEquals(descriptor.documentSha256,approvedDigest); approvals++; approveGate?.invoke(); approveFailure?.let { throw it }
            return ProcessorLibraryRegistration(descriptor,account).also { accepted=it }
        }
    }
    private class Owner {
        val fatal = CompletableDeferred<Throwable>()
        val scope = CoroutineScope(SupervisorJob()+Dispatchers.Unconfined+CoroutineExceptionHandler { _, error -> fatal.complete(error) })
        suspend fun close() { scope.coroutineContext.job.cancelAndJoin() }
    }
    private suspend fun ready(flow: ProcessorLibraryConnectionFlow) = withTimeout(5000) { flow.state.first { it.phase==ProcessorLibraryConnectionPhase.IDLE } }
    private suspend fun reviewing(flow: ProcessorLibraryConnectionFlow) = withTimeout(5000) { flow.state.first { it.phase==ProcessorLibraryConnectionPhase.REVIEW } }
    private fun authorize(flow: ProcessorLibraryConnectionFlow,email: String,launch: (PendingIntent)->Unit) {
        val identity=flow.attachConsentLauncher(launch)
        flow.activateConsentLauncher(identity)
        flow.authorize(email)
    }
    private fun consent(): ProcessorLibraryAuthorization.Consent = ProcessorLibraryAuthorization.Consent(PendingIntent.getActivity(RuntimeEnvironment.getApplication(),7,Intent("synthetic-google-consent"),PendingIntent.FLAG_IMMUTABLE))

    @Test fun previewRequiresExplicitAcknowledgmentAndApproval() = runBlocking {
        val owner=Owner(); val access=Access(descriptor()); val saved=SavedStateHandle(); val flow=ProcessorLibraryConnectionFlow(saved,owner.scope,access)
        try {
            ready(flow); flow.draft(access.descriptor.encode()); authorize(flow,access.account.email) { fail("Unexpected consent") }; reviewing(flow)
            assertEquals(0,access.approvals); flow.approve(); assertEquals(0,access.approvals)
            flow.acknowledge(true); flow.approve(); val accepted=ready(flow)
            assertEquals(1,access.approvals); assertEquals(access.descriptor.encode(),accepted.accepted?.descriptor?.encode()); assertNull(accepted.review)
            assertEquals(setOf("processor.connection.draft"),saved.keys())
            assertFalse(ProcessorLibraryAuthorization.Granted("sensitive-token").toString().contains("sensitive-token"))
        } finally { owner.close() }
    }
    @Test fun malformedCodeNeverRequestsGoogleAuthorization() = runBlocking {
        val owner=Owner(); val access=Access(descriptor()); val flow=ProcessorLibraryConnectionFlow(SavedStateHandle(),owner.scope,access)
        try { ready(flow); flow.draft("not a connection"); authorize(flow,access.account.email) { fail("Unexpected consent") }
            withTimeout(5000) { flow.state.first { it.message!=null } }; assertEquals(0,access.authorizations); assertEquals(0,access.reviews); assertEquals(0,access.approvals)
        } finally { owner.close() }
    }
    @Test fun invalidAccountSelectorCannotRequestGoogleAccess() = runBlocking {
        val owner=Owner(); val access=Access(descriptor()); val flow=ProcessorLibraryConnectionFlow(SavedStateHandle(),owner.scope,access)
        try {
            ready(flow); flow.draft(access.descriptor.encode()); authorize(flow,"bad\nselector") { fail("Invalid account consent launched") }
            withTimeout(5000) { flow.state.first { it.message!=null } }; assertEquals(0,access.authorizations); assertEquals(0,access.reviews)
        } finally { owner.close() }
    }
    @Test fun savedStateReadRetriesWithoutDroppingPriorTrust() = runBlocking {
        val owner=Owner(); val access=Access(descriptor()); access.currentFailure=IOException("synthetic local read failure")
        val flow=ProcessorLibraryConnectionFlow(SavedStateHandle(),owner.scope,access)
        try {
            withTimeout(5000) { flow.state.first { it.message!=null } }; assertNull(flow.state.value.accepted)
            access.currentFailure=null; access.accepted=ProcessorLibraryRegistration(access.descriptor,access.account); flow.refresh()
            val loaded=ready(flow); assertEquals(access.accepted,loaded.accepted)
            access.accepted=null; flow.refresh()
            val blocked=withTimeout(5000) { flow.state.first { it.phase==ProcessorLibraryConnectionPhase.BLOCKED } }
            assertEquals(loaded.accepted,blocked.accepted); assertEquals(0,access.authorizations)
        } finally { owner.close() }
    }
    @Test fun aCallbackWithoutItsCapturedRequestCannotReadOrApprove() = runBlocking {
        val owner=Owner(); val access=Access(descriptor()); val flow=ProcessorLibraryConnectionFlow(SavedStateHandle(),owner.scope,access)
        try { ready(flow); flow.returned(Activity.RESULT_OK,Intent()); withTimeout(5000) { flow.state.first { it.message!=null } }
            assertEquals(0,access.returns); assertEquals(0,access.reviews); assertEquals(0,access.approvals)
        } finally { owner.close() }
    }
    @Test fun recreatedConsentJoinsInitialReadAndStillRequiresFreshApproval() = runBlocking {
        val first=Owner(); val access=Access(descriptor()); val saved=SavedStateHandle(); access.authorization=consent()
        val flow=ProcessorLibraryConnectionFlow(saved,first.scope,access)
        ready(flow); flow.draft(access.descriptor.encode()); val launched=CompletableDeferred<Unit>()
        authorize(flow,access.account.email) { launched.complete(Unit) }; withTimeout(5000) { launched.await() }; first.close()
        val restored=SavedStateHandle(saved.keys().associateWith { saved.get<Any?>(it) }); val second=Owner(); val release=CompletableDeferred<Unit>()
        try {
            access.currentGate={ release.await() }; val recreated=ProcessorLibraryConnectionFlow(restored,second.scope,access)
            recreated.returned(Activity.RESULT_OK,Intent()); assertEquals(0,access.returns); release.complete(Unit)
            val preview=reviewing(recreated); assertFalse(preview.acknowledged); assertEquals(0,access.approvals); assertEquals(1,access.returns)
            recreated.approve(); assertEquals(0,access.approvals)
        } finally { release.complete(Unit); second.close() }
    }
    private class ScreenOwner : LifecycleOwner {
        val registry=LifecycleRegistry.createUnsafe(this)
        override val lifecycle: Lifecycle get()=registry
    }
    @Test fun stoppedConsentHostWaitsForForegroundAndRetiresPermanently() = runBlocking {
        val owner=Owner();val access=Access(descriptor());access.authorization=consent()
        val flow=ProcessorLibraryConnectionFlow(SavedStateHandle(),owner.scope,access);val screen=ScreenOwner();var calls=0
        val host=ProcessorLibraryConsentHost(screen.lifecycle,flow) { calls++ }
        try {
            ready(flow);flow.draft(access.descriptor.encode());flow.authorize(access.account.email)
            withTimeout(5000) { flow.state.first { it.phase==ProcessorLibraryConnectionPhase.GOOGLE_CONSENT } }
            assertEquals(0,calls);screen.registry.currentState=Lifecycle.State.STARTED;assertEquals(1,calls)
            screen.registry.currentState=Lifecycle.State.CREATED;screen.registry.currentState=Lifecycle.State.STARTED;assertEquals(1,calls)
            host.close();screen.registry.currentState=Lifecycle.State.DESTROYED;assertEquals(1,calls)
        } finally { host.close();owner.close() }
    }
    @Test fun aClosedHostCannotConsumeConsentForItsReplacement() = runBlocking {
        val owner=Owner();val access=Access(descriptor());access.authorization=consent()
        val flow=ProcessorLibraryConnectionFlow(SavedStateHandle(),owner.scope,access);val oldScreen=ScreenOwner();val newScreen=ScreenOwner()
        var oldCalls=0;var newCalls=0
        val old=ProcessorLibraryConsentHost(oldScreen.lifecycle,flow) { oldCalls++ }
        try {
            old.close();oldScreen.registry.currentState=Lifecycle.State.STARTED
            ready(flow);flow.draft(access.descriptor.encode());flow.authorize(access.account.email)
            withTimeout(5000) { flow.state.first { it.phase==ProcessorLibraryConnectionPhase.GOOGLE_CONSENT } }
            val replacement=ProcessorLibraryConsentHost(newScreen.lifecycle,flow) { newCalls++ }
            try { newScreen.registry.currentState=Lifecycle.State.STARTED;assertEquals(0,oldCalls);assertEquals(1,newCalls) }
            finally { replacement.close() }
        } finally { old.close();owner.close() }
    }
    @Test fun closingInsideLaunchCannotRetainAClosedHost() = runBlocking {
        val owner=Owner();val access=Access(descriptor());access.authorization=consent()
        val flow=ProcessorLibraryConnectionFlow(SavedStateHandle(),owner.scope,access);val screen=ScreenOwner();var calls=0
        var host: ProcessorLibraryConsentHost?=null
        try {
            ready(flow);flow.draft(access.descriptor.encode());flow.authorize(access.account.email)
            withTimeout(5000) { flow.state.first { it.phase==ProcessorLibraryConnectionPhase.GOOGLE_CONSENT } }
            screen.registry.currentState=Lifecycle.State.CREATED
            host=ProcessorLibraryConsentHost(screen.lifecycle,flow) { calls++;checkNotNull(host).close() }
            screen.registry.currentState=Lifecycle.State.STARTED
            assertEquals(1,calls)
            flow.returned(Activity.RESULT_OK,Intent());reviewing(flow)
            flow.draft(access.descriptor.encode());flow.authorize(access.account.email)
            withTimeout(5000) { flow.state.first { it.phase==ProcessorLibraryConnectionPhase.GOOGLE_CONSENT } }
            withTimeout(5000) { owner.scope.coroutineContext.job.children.toList().forEach { it.join() } }
            assertEquals(1,calls)
            val replacementScreen=ScreenOwner();replacementScreen.registry.currentState=Lifecycle.State.STARTED
            var replacementCalls=0
            val replacement=ProcessorLibraryConsentHost(replacementScreen.lifecycle,flow) { replacementCalls++ }
            try { assertEquals(1,replacementCalls);assertEquals(1,calls) } finally { replacement.close() }
        } finally { host?.close();owner.close() }
    }
    @Test fun rotationRebindsConsentWithoutCallingTheRetiredScreen() = runBlocking {
        val owner=Owner(); val access=Access(descriptor()); access.authorization=consent()
        val flow=ProcessorLibraryConnectionFlow(SavedStateHandle(),owner.scope,access)
        val entered=CompletableDeferred<Unit>();val release=CompletableDeferred<Unit>();val delivered=CompletableDeferred<Unit>()
        var oldCalls=0;var newCalls=0
        try {
            ready(flow);flow.draft(access.descriptor.encode())
            access.authorizeGate={ entered.complete(Unit);withContext(NonCancellable) { release.await() } }
            val old=flow.attachConsentLauncher { oldCalls++ };flow.authorize(access.account.email)
            withTimeout(5000) { entered.await() };flow.detachConsentLauncher(old)
            val replacement=flow.attachConsentLauncher { newCalls++;delivered.complete(Unit) }
            flow.activateConsentLauncher(replacement)
            flow.detachConsentLauncher(old) // Old disposal cannot retire the replacement host.
            release.complete(Unit);withTimeout(5000) { delivered.await() }
            assertEquals(0,oldCalls);assertEquals(1,newCalls);assertEquals(0,access.reviews);assertEquals(0,access.approvals)
        } finally { release.complete(Unit);owner.close() }
    }
    @Test fun consentWaitsForAnAttachedScreenAndIsDeliveredOnlyOnce() = runBlocking {
        val owner=Owner();val access=Access(descriptor());access.authorization=consent()
        val saved=SavedStateHandle();val flow=ProcessorLibraryConnectionFlow(saved,owner.scope,access);var calls=0
        try {
            ready(flow);flow.draft(access.descriptor.encode());flow.authorize(access.account.email)
            withTimeout(5000) { flow.state.first { it.phase==ProcessorLibraryConnectionPhase.GOOGLE_CONSENT } }
            val delivered=CompletableDeferred<Unit>();val first=flow.attachConsentLauncher { calls++;delivered.complete(Unit) }
            flow.activateConsentLauncher(first)
            withTimeout(5000) { delivered.await() };flow.detachConsentLauncher(first)
            val replacement=flow.attachConsentLauncher { calls++ };flow.activateConsentLauncher(replacement);assertEquals(1,calls)
            assertTrue(saved.keys().all { saved.get<Any?>(it) is String });assertEquals(0,access.approvals)
        } finally { owner.close() }
    }
    @Test fun operationalFailureKeepsAcceptedStateAndClearsHumanAcknowledgment() = runBlocking {
        val owner=Owner(); val access=Access(descriptor()); access.accepted=ProcessorLibraryRegistration(access.descriptor,access.account)
        val flow=ProcessorLibraryConnectionFlow(SavedStateHandle(),owner.scope,access)
        try {
            ready(flow); authorize(flow,access.account.email) { fail("Unexpected consent") }; reviewing(flow)
            access.approveFailure=IOException("synthetic transport failure"); flow.acknowledge(true); flow.approve()
            val failure=withTimeout(5000) { flow.state.first { it.message!=null } }
            assertEquals(access.accepted,failure.accepted); assertFalse(failure.acknowledged); assertEquals(1,access.approvals)
            flow.approve(); assertEquals(1,access.approvals)
        } finally { owner.close() }
    }
    @Test fun cancellationKeepsBusyUntilTheActualOwnerRetires() = runBlocking {
        val owner=Owner(); val access=Access(descriptor()); val flow=ProcessorLibraryConnectionFlow(SavedStateHandle(),owner.scope,access)
        val entered=CompletableDeferred<Unit>(); val release=CompletableDeferred<Unit>()
        try {
            access.authorizeGate={ entered.complete(Unit); withContext(NonCancellable) { release.await() } }
            ready(flow); flow.draft(access.descriptor.encode()); authorize(flow,access.account.email) { fail("Canceled consent launched") }
            withTimeout(5000) { entered.await() }; flow.cancelRead(); assertTrue(flow.state.value.busy)
            authorize(flow,access.account.email) { fail("Overlapping consent launched") }; assertEquals(1,access.authorizations)
            release.complete(Unit); ready(flow); assertEquals(0,access.reviews); assertNull(flow.state.value.accepted)
        } finally { release.complete(Unit); owner.close() }
    }
    @Test fun aReentrantApprovalCannotBeResetByItsPredecessorFinally() = runBlocking {
        val owner=Owner(); val access=Access(descriptor()); val flow=ProcessorLibraryConnectionFlow(SavedStateHandle(),owner.scope,access)
        val entered=CompletableDeferred<Unit>(); val release=CompletableDeferred<Unit>()
        try {
            ready(flow); flow.draft(access.descriptor.encode()); access.approveGate={ entered.complete(Unit); release.await() }
            val collector=owner.scope.launch(start=CoroutineStart.UNDISPATCHED) { reviewing(flow); flow.acknowledge(true); flow.approve() }
            authorize(flow,access.account.email) { fail("Unexpected consent") }; withTimeout(5000) { entered.await() }
            assertEquals(ProcessorLibraryConnectionPhase.APPROVING,flow.state.value.phase); assertTrue(flow.state.value.busy)
            release.complete(Unit); ready(flow); collector.join(); assertEquals(1,access.approvals)
        } finally { release.complete(Unit); owner.close() }
    }
    @Test fun corruptionStopsConnectionCommandsAndProgrammingErrorsPropagate() = runBlocking {
        val owner=Owner(); val access=Access(descriptor()); access.currentFailure=ProcessorLibraryStateCorrupt("synthetic damaged pin")
        val flow=ProcessorLibraryConnectionFlow(SavedStateHandle(),owner.scope,access)
        try {
            withTimeout(5000) { flow.state.first { it.phase==ProcessorLibraryConnectionPhase.BLOCKED } }
            flow.draft(access.descriptor.encode()); authorize(flow,access.account.email) { fail("Blocked consent launched") }; flow.approve()
            assertEquals(0,access.authorizations); assertEquals(0,access.approvals); assertFalse(owner.fatal.isCompleted)
        } finally { owner.close() }
        val broken=Owner(); val bad=Access(descriptor()); bad.currentFailure=IllegalStateException("synthetic invariant")
        try { ProcessorLibraryConnectionFlow(SavedStateHandle(),broken.scope,bad)
            assertEquals("synthetic invariant",withTimeout(5000) { broken.fatal.await() }.message)
        } finally { broken.close() }
    }
}
