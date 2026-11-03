package com.myphotosanywhere.mpa.processor.client

import android.accounts.AccountManager
import android.app.Activity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.LocalActivity
import androidx.activity.result.IntentSenderRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.viewmodel.compose.viewModel
import com.google.android.gms.common.AccountPicker
import com.myphotosanywhere.mpa.R
import com.myphotosanywhere.mpa.ui.MotionVisibility
import com.myphotosanywhere.mpa.ui.MotionEdge

@Composable
internal fun ProcessorLibraryConnectionScreen(model: ProcessorLibraryConnectionViewModel = viewModel()) {
    val flow = model.connection
    val state by flow.state.collectAsStateWithLifecycle()
    val activity = LocalActivity.current
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    val resolution = rememberLauncherForActivityResult(ActivityResultContracts.StartIntentSenderForResult()) { flow.returned(it.resultCode,it.data) }
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) {
        val email = it.data?.getStringExtra(AccountManager.KEY_ACCOUNT_NAME)
        if (it.resultCode == Activity.RESULT_OK && !email.isNullOrBlank()) flow.authorize(email)
    }
    DisposableEffect(flow,resolution,activity,lifecycle) {
        val host = ProcessorLibraryConsentHost(lifecycle,flow) { pending ->
            try { resolution.launch(IntentSenderRequest.Builder(pending.intentSender).build()) }
            catch (_: android.content.ActivityNotFoundException) { flow.consentLaunchFailed() }
            catch (_: SecurityException) { flow.consentLaunchFailed() }
        }
        onDispose {
            host.close()
            if (activity?.isChangingConfigurations != true) flow.cancelRead()
        }
    }
    ProcessorLibraryConnectionContent(state,flow::draft,flow::acknowledge,flow::approve,chooseAccount={
        try { picker.launch(AccountPicker.newChooseAccountIntent(AccountPicker.AccountChooserOptions.Builder().setAllowableAccountsTypes(listOf("com.google")).build())) }
        catch (_: android.content.ActivityNotFoundException) { flow.report("Google account selection is unavailable. Install or update Google Play services.") }
        catch (_: SecurityException) { flow.report("Android blocked Google account selection. Check your account restrictions.") }
    },reviewAgain={ state.review?.account?.email?.let(flow::authorize) },refresh=flow::refresh)
}

@Composable
internal fun ProcessorLibraryConnectionContent(state: ProcessorLibraryConnectionState,changeCode: (String)->Unit,
    acknowledge: (Boolean)->Unit,approve: ()->Unit,chooseAccount: ()->Unit,reviewAgain: ()->Unit,refresh: ()->Unit) {
    Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).padding(20.dp).testTag("processor:connection"),verticalArrangement=Arrangement.spacedBy(16.dp)) {
        Text(stringResource(R.string.processor_connection_title),style=MaterialTheme.typography.headlineMedium,modifier=Modifier.semantics { heading() })
        Text(stringResource(R.string.processor_connection_explanation))
        Box(Modifier.fillMaxWidth().height(8.dp)) {
            MotionVisibility(state.busy,MotionEdge.TOP) { LinearProgressIndicator(Modifier.fillMaxWidth().testTag("processor:connection-progress")) }
        }
        state.accepted?.let { accepted ->
            Column(Modifier.fillMaxWidth().testTag("processor:accepted"),verticalArrangement=Arrangement.spacedBy(8.dp)) {
                Text(stringResource(R.string.processor_connection_approved),style=MaterialTheme.typography.titleMedium,modifier=Modifier.semantics { heading() })
                Text(accepted.account?.email ?: stringResource(R.string.processor_connection_choose_saved_account))
                Text(stringResource(R.string.processor_connection_catalog_separate))
            }
        }
        state.message?.let { Text(it,modifier=Modifier.testTag("processor:connection-message").semantics { liveRegion=LiveRegionMode.Polite }) }
        if (state.message != null && state.phase == ProcessorLibraryConnectionPhase.IDLE)
            TextButton(onClick=refresh,modifier=Modifier.heightIn(min=48.dp).testTag("processor:refresh")) { Text(stringResource(R.string.processor_connection_refresh)) }
        if (state.phase != ProcessorLibraryConnectionPhase.BLOCKED) {
            if (state.accepted == null) OutlinedTextField(value=state.draft,onValueChange=changeCode,enabled=state.editable,
                label={ Text(stringResource(R.string.processor_connection_code)) },supportingText={ Text(stringResource(R.string.processor_connection_code_help)) },
                modifier=Modifier.fillMaxWidth().testTag("processor:connection-code"),minLines=3,maxLines=5)
            Button(enabled=state.editable && state.draft.isNotBlank(),onClick=chooseAccount,modifier=Modifier.heightIn(min=48.dp).testTag("processor:choose-account")) {
                Text(stringResource(R.string.processor_connection_choose_account))
            }
        }
        if (state.phase == ProcessorLibraryConnectionPhase.GOOGLE_CONSENT)
            Text(stringResource(R.string.processor_connection_google_pending),modifier=Modifier.semantics { liveRegion=LiveRegionMode.Polite })
        state.review?.let { review ->
            Column(Modifier.fillMaxWidth().testTag("processor:review"),verticalArrangement=Arrangement.spacedBy(12.dp)) {
                Text(stringResource(R.string.processor_connection_review),style=MaterialTheme.typography.titleLarge,modifier=Modifier.semantics { heading() })
                Text(review.workspace.name,style=MaterialTheme.typography.titleMedium)
                Text(review.account.email)
                Text(stringResource(R.string.processor_connection_fingerprint))
                SelectionContainer { Text(review.descriptor.controllerKeyId,style=MaterialTheme.typography.bodySmall,modifier=Modifier.testTag("processor:fingerprint")) }
                Row(modifier=Modifier.fillMaxWidth().heightIn(min=48.dp).toggleable(value=state.acknowledged,
                    enabled=state.phase == ProcessorLibraryConnectionPhase.REVIEW,role=Role.Checkbox,onValueChange=acknowledge)
                    .testTag("processor:confirm-source").semantics(mergeDescendants=true) {},verticalAlignment=Alignment.CenterVertically) {
                    Checkbox(checked=state.acknowledged,onCheckedChange=null)
                    Text(stringResource(R.string.processor_connection_confirm_source),modifier=Modifier.weight(1f))
                }
                Button(enabled=state.phase == ProcessorLibraryConnectionPhase.REVIEW && state.acknowledged,onClick=approve,
                    modifier=Modifier.heightIn(min=48.dp).testTag("processor:approve")) { Text(stringResource(R.string.processor_connection_approve)) }
                TextButton(enabled=state.phase == ProcessorLibraryConnectionPhase.REVIEW,onClick=reviewAgain,modifier=Modifier.heightIn(min=48.dp)) {
                    Text(stringResource(R.string.processor_connection_review_again))
                }
            }
        }
    }
}
