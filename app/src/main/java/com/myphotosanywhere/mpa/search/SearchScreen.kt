package com.myphotosanywhere.mpa.search

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.grid.*
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.myphotosanywhere.mpa.R
import com.myphotosanywhere.mpa.data.Album
import com.myphotosanywhere.mpa.drive.DriveSource
import com.myphotosanywhere.mpa.library.*
import com.myphotosanywhere.mpa.selection.SelectionTarget
import com.myphotosanywhere.mpa.timeline.*
import com.myphotosanywhere.mpa.ui.ShellIcon
import java.time.LocalDate

@Composable
internal fun SearchTopBar(text: String,filtered: Boolean,onText: (String)->Unit,onSubmit: ()->Unit,onBack: ()->Unit,onFilters: ()->Unit) {
    val focus=remember { FocusRequester() }
    Row(Modifier.fillMaxWidth().statusBarsPadding().padding(horizontal=8.dp,vertical=4.dp).testTag("search:header"),verticalAlignment=Alignment.CenterVertically) {
        IconButton(onClick=onBack,modifier=Modifier.size(48.dp).testTag("search:back")) { ShellIcon(R.drawable.ic_shell_back,"Back") }
        OutlinedTextField(text,onText,modifier=Modifier.weight(1f).focusRequester(focus).testTag("search:query"),
            label={ Text("Search your photos",maxLines=1,overflow=TextOverflow.Ellipsis) },singleLine=true,keyboardOptions=KeyboardOptions(imeAction=ImeAction.Search),
            keyboardActions=KeyboardActions(onSearch={ onSubmit() }),trailingIcon={
                if(text.isNotEmpty()) IconButton(onClick={ onText("") },modifier=Modifier.size(48.dp).testTag("search:clear")) {
                    ShellIcon(R.drawable.ic_search_clear,"Clear search")
                }
            })
        IconButton(onClick=onFilters,modifier=Modifier.size(48.dp).testTag("shell:filters")) {
            BadgedBox(badge={ if(filtered) Badge() }) { ShellIcon(R.drawable.ic_shell_filter,if(filtered) "Search filters applied" else "Filter search") }
        }
    }
    LaunchedEffect(Unit) { if(text.isBlank() && !filtered) focus.requestFocus() }
}

@Composable
internal fun SearchScreen(draft: SearchCriteria,applied: SearchCriteria,recents: List<String>,model: SearchViewModel,
    timeline: TimelineViewModel,features: LibraryFeaturesModel,suggestions: LazyGridState,
    albums: List<Album>,sources: List<DriveSource>,bottomClearance: Dp,selection: List<SelectionTarget>,
    onToggle: (SelectionTarget)->Unit,selectionLocked: Boolean,onFilters: ()->Unit,onPeople: ()->Unit,onPlaces: ()->Unit,
    onAlbums: ()->Unit,onSubmit: ()->Unit,onConnect: ()->Unit,modifier: Modifier=Modifier,onRangeChange: ((List<SelectionTarget>)->Unit)?=null,
    onBeginSelection: (SelectionTarget)->Unit=onToggle,onSelectionLimit: ()->Unit={}) {
    val settings by features.settings.collectAsStateWithLifecycle()
    val zone=deviceTimelineZone(timeline)
    val query=remember(applied,zone) { applied.timeline(zone) }
    Column(modifier.testTag("search:screen")) {
        if(draft!=applied) LinearProgressIndicator(Modifier.fillMaxWidth().testTag("search:pending"))
        if(applied.browsing) {
            Row(Modifier.fillMaxWidth().padding(horizontal=12.dp),verticalAlignment=Alignment.CenterVertically) {
                Text("Newest first",Modifier.weight(1f),style=MaterialTheme.typography.labelLarge)
                TextButton(onClick=onFilters,modifier=Modifier.heightIn(min=48.dp)) { Text(if(applied.filtered) "Filters applied" else "Filters") }
                if(applied.filtered) TextButton(onClick=model::clearFilters,modifier=Modifier.heightIn(min=48.dp).testTag("search:clear-filters")) { Text("Clear filters") }
            }
            if(applied.filtered) Text(buildList {
                if(applied.type!=TimelineMediaType.ALL) add(if(applied.type==TimelineMediaType.VIDEOS) "Videos" else "Photos")
                if(applied.filter!="All") add(if(applied.filter=="Offline") "Offline originals" else applied.filter)
                applied.source?.let { key -> add(sources.find { it.key==key }?.folderName ?: "Source disconnected") }
                applied.dates?.let { add("${it.first} – ${it.last}") }
            }.joinToString(" · "),Modifier.padding(horizontal=12.dp,vertical=4.dp).testTag("search:filter-summary"),style=MaterialTheme.typography.bodySmall)
            TimelineGrid(query,timeline,features,bottomClearance,Modifier.weight(1f).fillMaxWidth(),selection,onToggle,selectionLocked,
                sources.isNotEmpty(),onConnect,onClearFilters={
                    if(applied.filtered) model.clearFilters() else { model.text("");model.submit() }
                },onRefresh=features::refreshCloud,emptyActionLabel=if(applied.filtered) "Clear filters" else "Clear search",onRangeChange=onRangeChange,onBeginSelection=onBeginSelection,onSelectionLimit=onSelectionLimit)
        } else {
            val repository=remember(features) { CollectionsRepository(features.app.database,features.app.timeline) }
            LazyVerticalGrid(GridCells.Adaptive(148.dp),state=suggestions,modifier=Modifier.fillMaxSize().testTag("search:suggestions"),
                contentPadding=PaddingValues(start=16.dp,top=12.dp,end=16.dp,bottom=16.dp+bottomClearance),
                verticalArrangement=Arrangement.spacedBy(12.dp),horizontalArrangement=Arrangement.spacedBy(12.dp)) {
                if(recents.isNotEmpty()) {
                    item(span={ GridItemSpan(maxLineSpan) }) { Row(verticalAlignment=Alignment.CenterVertically) {
                        Text("Recent searches",Modifier.weight(1f).semantics { heading() },style=MaterialTheme.typography.titleMedium)
                        TextButton(onClick=model::clearHistory,modifier=Modifier.heightIn(min=48.dp).testTag("search:clear-recents")) { Text("Clear recent searches") }
                    } }
                    items(recents,key={ "recent:$it" },span={ GridItemSpan(maxLineSpan) }) { recent ->
                        TextButton(onClick={ model.text(recent);onSubmit() },modifier=Modifier.fillMaxWidth().heightIn(min=48.dp).testTag("search:recent:$recent")) {
                            Text(recent,maxLines=2,overflow=TextOverflow.Ellipsis)
                        }
                    }
                }
                item(span={ GridItemSpan(maxLineSpan) }) { Text("Explore",Modifier.semantics { heading() },style=MaterialTheme.typography.titleLarge) }
                item { CollectionCard("Favorites",R.drawable.ic_shell_favorite,repository,TimelineQuery("Favorites"),{
                    model.update(draft.copy(filter="Favorites"));onSubmit()
                },"search:category:Favorites",compact=true) }
                item { CollectionCard("Videos",R.drawable.ic_shell_video,repository,TimelineQuery("Videos"),{
                    model.update(draft.copy(type=TimelineMediaType.VIDEOS));onSubmit()
                },"search:category:Videos",compact=true) }
                item { Column {
                    CollectionCard("People",R.drawable.ic_shell_people,repository,null,onPeople,"search:category:People",compact=true)
                    if(!settings.faceIndexing) Text("Recognition is off",style=MaterialTheme.typography.bodySmall)
                } }
                item { CollectionCard("Places",R.drawable.ic_shell_places,repository,null,onPlaces,"search:category:Places",compact=true) }
                if(albums.isNotEmpty()) item { CollectionCard("Albums",R.drawable.ic_shell_collections,repository,TimelineQuery(album=albums.first().id),onAlbums,"search:category:Albums",compact=true) }
                else item { OutlinedButton(onClick=onAlbums,modifier=Modifier.heightIn(min=48.dp).testTag("search:category:Albums")) { Text("Albums") } }
                item { CollectionCard("Edited photos",R.drawable.ic_shell_edit,repository,TimelineQuery("Edits"),{
                    model.update(draft.copy(filter="Edits"));onSubmit()
                },"search:category:Edited",compact=true) }
                item(span={ GridItemSpan(maxLineSpan) }) { OutlinedButton(onClick={
                    val today=LocalDate.now(zone);model.update(draft.copy(dates=SearchDates(today,today)));onSubmit()
                },modifier=Modifier.heightIn(min=48.dp).testTag("search:today")) { Text("Today") } }
                item(span={ GridItemSpan(maxLineSpan) }) { Text("Search indexed names, dates, albums, people, places and available labels.",style=MaterialTheme.typography.bodySmall) }
            }
        }
    }
}
