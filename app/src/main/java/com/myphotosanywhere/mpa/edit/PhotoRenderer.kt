package com.myphotosanywhere.mpa.edit

import androidx.core.graphics.createBitmap
import android.graphics.*
import com.myphotosanywhere.mpa.data.PhotoRepository
import java.io.File
import kotlin.math.pow

object PhotoRenderer {
    const val EXPORT_DIMENSION = 4096
    fun render(file: File, recipe: EditRecipe, maximum: Int = EXPORT_DIMENSION): Bitmap {
        recipe.validate()
        var current = PhotoRepository.preview(file, maximum)
        try {
            val x = (current.width * recipe.left).toInt().coerceIn(0,current.width-1)
            val y = (current.height * recipe.top).toInt().coerceIn(0,current.height-1)
            val w = ((recipe.right-recipe.left)*current.width).toInt().coerceIn(1,current.width-x)
            val h = ((recipe.bottom-recipe.top)*current.height).toInt().coerceIn(1,current.height-y)
            val cropped = Bitmap.createBitmap(current,x,y,w,h)
            if (cropped !== current) { current.recycle(); current = cropped }
            val matrix = Matrix().apply { setRotate(recipe.quarterTurns*90f+recipe.straighten) }
            val rotated = Bitmap.createBitmap(current,0,0,current.width,current.height,matrix,true)
            if (rotated !== current) { current.recycle(); current = rotated }
            // Crop the transparent corners introduced by straightening.
            if (recipe.straighten != 0f) {
                val radians = Math.toRadians(kotlin.math.abs(recipe.straighten).toDouble())
                val sin = kotlin.math.sin(radians); val cos = kotlin.math.cos(radians)
                val ow = if (recipe.quarterTurns%2==0) w else h
                val oh = if (recipe.quarterTurns%2==0) h else w
                val scale = minOf(ow/(ow*cos+oh*sin),oh/(oh*cos+ow*sin))
                val cw=(ow*scale).toInt().coerceIn(1,current.width)
                val ch=(oh*scale).toInt().coerceIn(1,current.height)
                val trimmed = Bitmap.createBitmap(current,(current.width-cw)/2,(current.height-ch)/2,cw,ch)
                if(trimmed !== current) { current.recycle(); current=trimmed }
            }
            val output = createBitmap(current.width,current.height,Bitmap.Config.ARGB_8888)
            try {
                val canvas=Canvas(output); canvas.drawColor(Color.BLACK)
                canvas.drawBitmap(current,0f,0f,Paint(Paint.ANTI_ALIAS_FLAG).apply{colorFilter=ColorMatrixColorFilter(colors(recipe))})
                recipe.strokes.forEach { stroke ->
                    val paint=Paint(Paint.ANTI_ALIAS_FLAG).apply {
                        color=stroke.color; strokeWidth=stroke.width*minOf(output.width,output.height)
                        style=Paint.Style.STROKE; strokeCap=Paint.Cap.ROUND; strokeJoin=Paint.Join.ROUND
                    }
                    val path=Path()
                    stroke.points.forEachIndexed { i,p ->
                        if(i==0) path.moveTo(p.x*output.width,p.y*output.height)
                        else path.lineTo(p.x*output.width,p.y*output.height)
                    }
                    if(stroke.points.size==1) {
                        val p=stroke.points.first(); paint.style=Paint.Style.FILL
                        canvas.drawCircle(p.x*output.width,p.y*output.height,paint.strokeWidth/2,paint)
                    } else canvas.drawPath(path,paint)
                }
                return output
            } catch(error: Exception) { output.recycle(); throw error }
        } finally { if(!current.isRecycled) current.recycle() }
    }
    private fun colors(r: EditRecipe): ColorMatrix {
        val matrix=ColorMatrix().apply{setSaturation(r.saturation)}
        val gain=2.0.pow(r.exposure.toDouble()).toFloat(); val c=r.contrast; val offset=128*(1-c)
        matrix.postConcat(ColorMatrix(floatArrayOf(
            c*gain*(1+r.warmth*.15f),0f,0f,0f,offset,0f,c*gain,0f,0f,offset,
            0f,0f,c*gain*(1-r.warmth*.15f),0f,offset,0f,0f,0f,1f,0f)))
        when(r.filter) {
            "Mono" -> matrix.postConcat(ColorMatrix().apply{setSaturation(0f)})
            "Sepia" -> matrix.postConcat(ColorMatrix(floatArrayOf(.393f,.769f,.189f,0f,0f,
                .349f,.686f,.168f,0f,0f,.272f,.534f,.131f,0f,0f,0f,0f,0f,1f,0f)))
            "Vivid" -> matrix.postConcat(ColorMatrix().apply{setSaturation(1.4f)})
            "Cool" -> matrix.postConcat(ColorMatrix(floatArrayOf(.92f,0f,0f,0f,0f,0f,1f,0f,0f,0f,
                0f,0f,1.12f,0f,0f,0f,0f,0f,1f,0f)))
        }
        return matrix
    }
}
