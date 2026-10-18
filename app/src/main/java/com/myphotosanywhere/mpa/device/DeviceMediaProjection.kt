package com.myphotosanywhere.mpa.device

/** Typed collections already select their media kind. Legacy views expose neither media_type
 * nor video orientation; requesting either would make an otherwise readable library fail. */
internal class DeviceMediaProjection(video: Boolean,sdk: Int) {
    val generation=sdk>=30
    val directory=if(sdk>=29) "relative_path" else "bucket_id"
    val columns=buildList {
        addAll(listOf("_id","_display_name","mime_type","_size","date_added","datetaken","date_modified","width","height"))
        if(!video || sdk>=29) add("orientation")
        if(generation) add("generation_modified")
        add(directory);add("bucket_display_name")
    }
    val selection=buildString {
        append("_id>? AND _size>0")
        if(sdk>=29) append(" AND is_pending=0")
        if(sdk>=30) append(" AND is_trashed=0")
    }
}
