package com.myphotosanywhere.mpa.library

import androidx.core.net.toUri
import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import com.myphotosanywhere.mpa.data.Photo
import org.osmdroid.config.Configuration
import org.osmdroid.tileprovider.tilesource.TileSourceFactory
import org.osmdroid.util.GeoPoint
import org.osmdroid.views.MapView
import org.osmdroid.views.overlay.Marker
import org.osmdroid.events.MapListener
import org.osmdroid.events.ScrollEvent
import org.osmdroid.events.ZoomEvent
import java.io.File
import kotlin.math.floor
import kotlin.math.pow

@Composable
fun PlacesScreen(photos: List<Photo>,online: Boolean,onOnline: (Boolean)->Unit,onPhoto: (String)->Unit) {
    val context=LocalContext.current
    val located=remember(photos) { photos.filter { LibrarySearch.validCoordinates(it.latitude,it.longitude) } }
    var query by remember { mutableStateOf("") }
    val visible=located.filter { query.isBlank() || it.placeLabel.contains(query,true) || it.displayName.contains(query,true) }
    val map=remember {
        Configuration.getInstance().apply {
            userAgentValue="MyPhotosAnywhere/0.3 (https://github.com/GreenFuze/MyPhotosAnywhere)"
            osmdroidBasePath=File(context.filesDir,"map")
            osmdroidTileCache=File(context.filesDir,"map/tiles")
            tileFileSystemCacheMaxBytes=64L*1024*1024
            tileFileSystemCacheTrimBytes=48L*1024*1024
        }
        MapView(context).apply { setTileSource(TileSourceFactory.MAPNIK); setMultiTouchControls(true); controller.setZoom(3.0); setUseDataConnection(false) }
    }
    var zoom by remember { mutableDoubleStateOf(3.0) }
    var cluster by remember { mutableStateOf<List<Photo>>(emptyList()) }
    DisposableEffect(map) {
        val listener=object: MapListener {
            override fun onScroll(event: ScrollEvent?) = false
            override fun onZoom(event: ZoomEvent?): Boolean { zoom=map.zoomLevelDouble; return false }
        }
        map.addMapListener(listener); map.onResume()
        onDispose { map.removeMapListener(listener); map.onPause(); map.onDetach() }
    }
    LaunchedEffect(visible,zoom,online) {
        map.setUseDataConnection(online)
        map.overlays.clear()
        val step=(360/2.0.pow(zoom+2)).coerceAtLeast(.00001)
        val groups=visible.groupBy { floor(requireNotNull(it.latitude)/step).toLong() to floor(requireNotNull(it.longitude)/step).toLong() }
        groups.values.forEach { group ->
            val marker=Marker(map).apply {
                position=GeoPoint(group.map { requireNotNull(it.latitude) }.average(),group.map { requireNotNull(it.longitude) }.average())
                title=if(group.size==1) group.first().displayName else "${group.size} photos"
                setAnchor(Marker.ANCHOR_CENTER,Marker.ANCHOR_BOTTOM)
                setOnMarkerClickListener { _,_ -> if(group.size==1) onPhoto(group.first().id) else cluster=group; true }
            }
            map.overlays.add(marker)
        }
        if(visible.isNotEmpty() && map.mapCenter.latitude==0.0 && map.mapCenter.longitude==0.0)
            map.controller.setCenter(GeoPoint(requireNotNull(visible.first().latitude),requireNotNull(visible.first().longitude)))
        map.invalidate()
    }
    Column(Modifier.fillMaxSize().padding(16.dp),verticalArrangement=Arrangement.spacedBy(8.dp)) {
        Text("Places · ${located.size} photos",style=MaterialTheme.typography.headlineSmall)
        OutlinedTextField(query,{ query=it },label={ Text("Search saved place names") },modifier=Modifier.fillMaxWidth(),singleLine=true)
        Row {
            Switch(checked=online,onCheckedChange=onOnline)
            Text("Load online map tiles",Modifier.padding(12.dp))
        }
        if(!online) Text("Photo locations work offline. Enable tiles to download the basemap; the tile service receives the viewed area.",style=MaterialTheme.typography.bodySmall)
        AndroidView(factory={ map },modifier=Modifier.fillMaxWidth().weight(1f))
        TextButton(onClick={ context.startActivity(Intent(Intent.ACTION_VIEW,"https://www.openstreetmap.org/copyright".toUri())) }) { Text("© OpenStreetMap contributors · ODbL") }
        LazyColumn(Modifier.heightIn(max=140.dp)) { items(visible,key={ it.id }) { photo ->
            TextButton(onClick={ onPhoto(photo.id) }) { Text("${photo.placeLabel.ifBlank { "%.3f, %.3f".format(photo.latitude,photo.longitude) }} · ${photo.displayName}") }
        } }
        if(located.isEmpty()) Text("No GPS locations found. Open photo details to add a place name or coordinates.")
    }
    if(cluster.isNotEmpty()) AlertDialog(onDismissRequest={ cluster=emptyList() },title={ Text("Photos at this location") },
        text={ LazyColumn { items(cluster,key={ it.id }) { photo -> TextButton(onClick={ onPhoto(photo.id); cluster=emptyList() }) { Text(photo.displayName) } } } },
        confirmButton={ TextButton(onClick={ cluster=emptyList() }) { Text("Close") } })
}
