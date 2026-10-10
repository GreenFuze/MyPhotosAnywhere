package com.myphotosanywhere.mpa.people

import android.content.Context
import android.graphics.*
import com.google.mlkit.vision.face.Face
import com.google.mlkit.vision.face.FaceLandmark
import org.tensorflow.lite.Interpreter
import org.tensorflow.lite.DataType
import java.nio.ByteBuffer
import java.nio.ByteOrder

/** Qualcomm MobileFaceNet v0.64.0: two RGB NCHW [0,1] inputs, two 128-dimensional outputs. */
class FaceEmbedding(context: Context): AutoCloseable {
    private val interpreter: Interpreter
    private val modelBuffer: ByteBuffer
    init {
        val bytes=context.assets.open("mobile_facenet.tflite").use { it.readBytes() }
        val hash=java.security.MessageDigest.getInstance("SHA-256").digest(bytes).joinToString(""){"%02x".format(it)}
        check(hash=="2046ab776fba2ec7e420767ffe7eb74a7402a8412d3d7ea8b9f4ca8420e5f3d7") { "Face model checksum mismatch." }
        modelBuffer=ByteBuffer.allocateDirect(bytes.size).order(ByteOrder.nativeOrder()).apply { put(bytes); rewind() }
        interpreter=Interpreter(modelBuffer,Interpreter.Options().setNumThreads(2))
        for(i in 0..1) check(interpreter.getInputTensor(i).shape().contentEquals(intArrayOf(1,3,112,112)) &&
            interpreter.getInputTensor(i).dataType()==DataType.FLOAT32) { "Face model input contract mismatch." }
        check(interpreter.getOutputTensor(0).shape().contentEquals(intArrayOf(2,128)))
    }
    fun aligned(source: Bitmap,face: Face): Bitmap? {
        if(kotlin.math.abs(face.headEulerAngleY)>25 || kotlin.math.abs(face.headEulerAngleX)>25) return null
        val ids=listOf(FaceLandmark.LEFT_EYE,FaceLandmark.RIGHT_EYE,FaceLandmark.NOSE_BASE,FaceLandmark.MOUTH_LEFT,FaceLandmark.MOUTH_RIGHT)
        val raw=ids.map { face.getLandmark(it)?.position ?: return null }
        val eyes=raw.take(2).sortedBy { it.x }; val mouths=raw.takeLast(2).sortedBy { it.x }
        val detected=listOf(eyes[0],eyes[1],raw[2],mouths[0],mouths[1])
        if(face.boundingBox.width()<64 || face.boundingBox.height()<64) return null
        // Match the source implementation's square reference with 25% inner padding.
        val base=listOf(PointF(30.2946f+8,51.6963f),PointF(65.5318f+8,51.5014f),PointF(48.0252f+8,71.7366f),
            PointF(33.5493f+8,92.3655f),PointF(62.7299f+8,92.2041f))
        val target=base.map { PointF((it.x+28)/1.5f,(it.y+28)/1.5f) }
        val from=floatArrayOf(detected[0].x,detected[0].y,detected[1].x,detected[1].y,detected[2].x,detected[2].y)
        val to=floatArrayOf(target[0].x,target[0].y,target[1].x,target[1].y,target[2].x,target[2].y)
        val matrix=Matrix(); if(!matrix.setPolyToPoly(from,0,to,0,3)) return null
        // Reject degenerate/skewed crops; matching remains a review suggestion.
        val mouth=floatArrayOf(detected[3].x,detected[3].y,detected[4].x,detected[4].y); matrix.mapPoints(mouth)
        if(kotlin.math.hypot(mouth[0]-target[3].x,mouth[1]-target[3].y)>14 ||
            kotlin.math.hypot(mouth[2]-target[4].x,mouth[3]-target[4].y)>14) return null
        val bitmap=Bitmap.createBitmap(112,112,Bitmap.Config.ARGB_8888)
        Canvas(bitmap).drawBitmap(source,matrix,Paint(Paint.ANTI_ALIAS_FLAG or Paint.FILTER_BITMAP_FLAG))
        return bitmap
    }
    fun embed(face: Bitmap): ByteArray {
        require(face.width==112 && face.height==112)
        val pixels=IntArray(112*112); face.getPixels(pixels,0,112,0,0,112,112)
        fun input()=ByteBuffer.allocateDirect(3*112*112*4).order(ByteOrder.nativeOrder()).apply {
            for(channel in 0..2) for(pixel in pixels) putFloat(((pixel shr ((2-channel)*8)) and 255)/255f)
            rewind()
        }
        val output=Array(2) { FloatArray(128) }
        interpreter.runForMultipleInputsOutputs(arrayOf(input(),input()),mutableMapOf<Int,Any>(0 to output))
        return FaceMatching.encode(output[0])
    }
    override fun close() { interpreter.close() }
}
