package com.myphotosanywhere.mpa.edit

import androidx.compose.foundation.Image
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.unit.dp
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel

@Composable
fun EditorScreen(photoId: String,onClose: ()->Unit,onSaved: (String)->Unit,model: EditorModel = viewModel()) {
    val recipe by model.recipe.collectAsStateWithLifecycle()
    val image by model.preview.collectAsStateWithLifecycle()
    val busy by model.busy.collectAsStateWithLifecycle()
    val error by model.error.collectAsStateWithLifecycle()
    val saved by model.savedId.collectAsStateWithLifecycle()
    val version by model.historyVersion.collectAsStateWithLifecycle()
    var markup by remember { mutableStateOf(false) }
    var stroke by remember { mutableStateOf<List<MarkPoint>>(emptyList()) }
    var color by remember { mutableStateOf(0xFFFF5252.toInt()) }
    LaunchedEffect(photoId) { model.open(photoId) }
    LaunchedEffect(saved) { saved?.let(onSaved) }
    DisposableEffect(photoId) { onDispose { model.close() } }
    Dialog(onDismissRequest={ if(!busy) onClose() },properties=DialogProperties(usePlatformDefaultWidth=false)) {
        Surface(Modifier.fillMaxSize()) {
            Column(Modifier.fillMaxSize().safeDrawingPadding()) {
                Row(Modifier.fillMaxWidth().padding(12.dp),horizontalArrangement=Arrangement.SpaceBetween) {
                    TextButton(enabled=!busy,onClick=onClose) { Text("Close") }
                    Text("Edit photo",style=MaterialTheme.typography.titleLarge,modifier=Modifier.padding(top=12.dp))
                    Button(modifier=Modifier.testTag("editor-save"),enabled=!busy && image!=null,onClick=model::save) { Text("Save copy") }
                }
                if(busy) LinearProgressIndicator(Modifier.fillMaxWidth())
                error?.let { Text(it,color=MaterialTheme.colorScheme.error,modifier=Modifier.padding(12.dp)) }
                Column(Modifier.weight(1f).verticalScroll(rememberScrollState()).padding(16.dp),verticalArrangement=Arrangement.spacedBy(12.dp)) {
                    image?.let { bitmap ->
                        Box(Modifier.fillMaxWidth().aspectRatio(bitmap.width.toFloat()/bitmap.height)) {
                            Image(bitmap.asImageBitmap(),"Edited preview",Modifier.fillMaxSize())
                            if(markup) Canvas(Modifier.fillMaxSize().pointerInput(markup,color,busy) {
                                detectDragGestures(onDragStart={ p -> stroke=listOf(MarkPoint((p.x/size.width).coerceIn(0f,1f),(p.y/size.height).coerceIn(0f,1f))) },
                                    onDragCancel={ stroke=emptyList() },onDragEnd={
                                        if(stroke.isNotEmpty() && model.recipe.value.strokes.size<100 && !busy)
                                            model.commit(model.recipe.value.copy(strokes=model.recipe.value.strokes+Stroke(stroke,color)))
                                        stroke=emptyList()
                                    }) { change,_ ->
                                    change.consume()
                                    if(stroke.size<4000) stroke=stroke+MarkPoint((change.position.x/size.width).coerceIn(0f,1f),(change.position.y/size.height).coerceIn(0f,1f))
                                }
                            }) {
                                stroke.zipWithNext().forEach { (a,b) -> drawLine(Color(color),
                                    androidx.compose.ui.geometry.Offset(a.x*size.width,a.y*size.height),
                                    androidx.compose.ui.geometry.Offset(b.x*size.width,b.y*size.height),minOf(size.width,size.height)*.012f) }
                            }
                        }
                    } ?: CircularProgressIndicator()
                    Row(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
                        // Read version so changing the history updates enabled states.
                        TextButton(enabled=!busy && version>=0 && model.canUndo,onClick=model::undo) { Text("Undo") }
                        TextButton(enabled=!busy && model.canRedo,onClick=model::redo) { Text("Redo") }
                        TextButton(enabled=!busy,onClick={ model.commit(EditRecipe()) }) { Text("Reset") }
                    }
                    Row(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
                        OutlinedButton(enabled=!busy,onClick={ model.commit(recipe.copy(quarterTurns=(recipe.quarterTurns+1)%4,strokes=emptyList())) }) { Text("Rotate 90°") }
                        FilterChip(selected=markup,onClick={ markup=!markup },label={ Text("Draw") })
                    }
                    if(markup) {
                        Text("Draw on the preview. Geometry changes clear markup.")
                        Row { listOf(0xFFFF5252.toInt(),0xFF2979FF.toInt(),0xFFFFFFFF.toInt(),0xFF000000.toInt()).forEach { c ->
                            FilterChip(selected=color==c,onClick={ color=c },label={ Text("●",color=Color(c)) })
                        } }
                    }
                    Text("Crop",style=MaterialTheme.typography.titleMedium)
                    EditorSlider("Left",recipe.left,0f..(recipe.right-.05f),busy,{ model.change(recipe.copy(left=it,strokes=emptyList())) },{ model.commit(model.recipe.value) })
                    EditorSlider("Right",recipe.right,(recipe.left+.05f)..1f,busy,{ model.change(recipe.copy(right=it,strokes=emptyList())) },{ model.commit(model.recipe.value) })
                    EditorSlider("Top",recipe.top,0f..(recipe.bottom-.05f),busy,{ model.change(recipe.copy(top=it,strokes=emptyList())) },{ model.commit(model.recipe.value) })
                    EditorSlider("Bottom",recipe.bottom,(recipe.top+.05f)..1f,busy,{ model.change(recipe.copy(bottom=it,strokes=emptyList())) },{ model.commit(model.recipe.value) })
                    EditorSlider("Straighten",recipe.straighten,-15f..15f,busy,{ model.change(recipe.copy(straighten=it,strokes=emptyList())) },{ model.commit(model.recipe.value) })
                    Text("Adjust",style=MaterialTheme.typography.titleMedium)
                    EditorSlider("Exposure",recipe.exposure,-2f..2f,busy,{ model.change(recipe.copy(exposure=it)) },{ model.commit(model.recipe.value) })
                    EditorSlider("Contrast",recipe.contrast,.5f..2f,busy,{ model.change(recipe.copy(contrast=it)) },{ model.commit(model.recipe.value) })
                    EditorSlider("Saturation",recipe.saturation,0f..2f,busy,{ model.change(recipe.copy(saturation=it)) },{ model.commit(model.recipe.value) })
                    EditorSlider("Warmth",recipe.warmth,-1f..1f,busy,{ model.change(recipe.copy(warmth=it)) },{ model.commit(model.recipe.value) })
                    Text("Filters",style=MaterialTheme.typography.titleMedium)
                    EditRecipe.FILTERS.forEach { filter ->
                        FilterChip(selected=recipe.filter==filter,enabled=!busy,onClick={ model.commit(recipe.copy(filter=filter)) },label={ Text(filter) })
                    }
                    Row { Switch(checked=recipe.keepLocation,enabled=!busy,onCheckedChange={ model.commit(recipe.copy(keepLocation=it)) }); Text("Keep GPS in edited copy",Modifier.padding(12.dp)) }
                    Text("Edits save as a separate JPEG up to 4096 pixels. Your original stays unchanged. Drafts are saved on this phone.",style=MaterialTheme.typography.bodySmall)
                }
            }
        }
    }
}
@Composable
private fun EditorSlider(name: String,value: Float,range: ClosedFloatingPointRange<Float>,busy: Boolean,
    change: (Float)->Unit,finish: ()->Unit) {
    Text("$name · %.2f".format(value),style=MaterialTheme.typography.bodySmall)
    Slider(value=value.coerceIn(range),valueRange=range,enabled=!busy,onValueChange=change,onValueChangeFinished=finish)
}
