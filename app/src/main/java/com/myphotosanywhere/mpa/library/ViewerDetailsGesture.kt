package com.myphotosanywhere.mpa.library

import kotlin.math.abs

/** One-finger upward details drag; a zoom, horizontal page swipe or second pointer owns its gesture. */
internal class ViewerDetailsGesture(private val distance: Float,private val slop: Float) {
    private enum class Owner { UNDECIDED, DETAILS, OTHER }
    private var owner=Owner.UNDECIDED
    private var x=0f
    private var y=0f
    init { require(distance>slop && slop>=0) }
    fun cancel() { owner=Owner.OTHER }
    fun move(dx: Float,dy: Float,pointers: Int,zoomed: Boolean,consumed: Boolean): Boolean {
        require(dx.isFinite() && dy.isFinite())
        if(pointers>1 || zoomed || (consumed && owner!=Owner.DETAILS)) cancel()
        if(owner==Owner.OTHER) return false
        x+=dx;y+=dy
        if(owner==Owner.UNDECIDED && maxOf(abs(x),abs(y))>slop) {
            owner=if(y<0 && abs(y)>abs(x)) Owner.DETAILS else Owner.OTHER
        }
        return owner==Owner.DETAILS
    }
    fun finish()=owner==Owner.DETAILS && -y>=distance
}
