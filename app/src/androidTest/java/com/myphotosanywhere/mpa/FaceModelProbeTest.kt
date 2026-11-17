package com.myphotosanywhere.mpa

import android.graphics.BitmapFactory
import android.util.Log
import androidx.core.graphics.scale
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.google.mlkit.vision.common.InputImage
import com.google.mlkit.vision.face.FaceDetection
import com.google.mlkit.vision.face.FaceDetectorOptions
import com.google.mlkit.vision.label.ImageLabeling
import com.google.mlkit.vision.label.defaults.ImageLabelerOptions
import com.myphotosanywhere.mpa.people.FaceEmbedding
import com.myphotosanywhere.mpa.people.FaceMatching
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.tasks.await
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith

/** Local diagnostic only: execute each mode in a new process to avoid once-only warning masking. */
@RunWith(AndroidJUnit4::class)
class FaceModelProbeTest {
    @Test fun isolatedModelCompletesWithValidatedOutput()=runBlocking<Unit> {
        val instrumentation=InstrumentationRegistry.getInstrumentation()
        val mode=requireNotNull(InstrumentationRegistry.getArguments().getString("mpaModelProbe"))
        val bitmap=instrumentation.context.assets.open("model-probe-grace-hopper.jpg").use { requireNotNull(BitmapFactory.decodeStream(it)) }
        try {
            Log.i("MPAModelProbe","BEGIN $mode ${bitmap.width}x${bitmap.height}")
            when(mode) {
                "embedding" -> {
                    val input=bitmap.scale(112,112,true)
                    try { FaceEmbedding(instrumentation.targetContext).use { model ->
                        val first=FaceMatching.decode(model.embed(input));val second=FaceMatching.decode(model.embed(input))
                        assertEquals(128,first.size);assertTrue(first.all { it.isFinite() })
                        assertEquals(1f,FaceMatching.cosine(first,second),.001f)
                        Log.i("MPAModelProbe","RESULT embedding128 finite deterministic")
                    } } finally { if(input!==bitmap) input.recycle() }
                }
                "face-fast","face-accurate","face-production" -> {
                    val options=FaceDetectorOptions.Builder().setPerformanceMode(
                        if(mode=="face-fast") FaceDetectorOptions.PERFORMANCE_MODE_FAST else FaceDetectorOptions.PERFORMANCE_MODE_ACCURATE)
                    if(mode=="face-production") options.setLandmarkMode(FaceDetectorOptions.LANDMARK_MODE_ALL).setMinFaceSize(.03f)
                    FaceDetection.getClient(options.build()).use { detector ->
                        val faces=detector.process(InputImage.fromBitmap(bitmap,0)).await()
                        assertTrue("Official face fixture must produce a face",faces.isNotEmpty())
                        assertTrue(faces.all { it.boundingBox.width()>0 && it.boundingBox.height()>0 })
                        Log.i("MPAModelProbe","RESULT $mode faces=${faces.size} bounds=${faces.map { it.boundingBox.toShortString() }}")
                    }
                }
                "labels" -> ImageLabeling.getClient(ImageLabelerOptions.Builder().setConfidenceThreshold(.7f).build()).use { labeler ->
                    val labels=labeler.process(InputImage.fromBitmap(bitmap,0)).await()
                    assertTrue(labels.isNotEmpty());assertTrue(labels.all { it.confidence.isFinite() })
                    Log.i("MPAModelProbe","RESULT labels ${labels.map { it.text }}")
                }
                else -> error("Unknown isolated model probe: $mode")
            }
            Log.i("MPAModelProbe","END $mode")
        } finally { bitmap.recycle() }
    }
}
