package com.myphotosanywhere.mpa.people

import java.time.Duration
import java.time.ZonedDateTime

/** Use phone wall time for window checks and instants for delays (including DST transitions). */
object IndexWindow {
    fun contains(minute: Int,start: Int,end: Int): Boolean {
        require(minute in 0..1439 && start in 0..1439 && end in 0..1439 && start!=end)
        return if(start<end) minute in start until end else minute>=start || minute<end
    }
    fun delay(now: ZonedDateTime,start: Int,end: Int): Duration {
        if(contains(now.hour*60+now.minute,start,end)) return Duration.ZERO
        var next=now.toLocalDate().atTime(start/60,start%60).atZone(now.zone)
        if(!next.isAfter(now)) next=now.toLocalDate().plusDays(1).atTime(start/60,start%60).atZone(now.zone)
        return Duration.between(now,next)
    }
}
