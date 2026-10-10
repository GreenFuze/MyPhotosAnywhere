package com.myphotosanywhere.mpa.edit

import org.junit.Assert.*
import org.junit.Test

class EditRecipeTest {
    @Test fun recipesRoundTripMarkupCropAndLocationChoice() {
        val recipe=EditRecipe(left=.1f,right=.9f,quarterTurns=1,exposure=.5f,keepLocation=true,
            strokes=listOf(Stroke(listOf(MarkPoint(.2f,.3f),MarkPoint(.4f,.5f)))))
        assertEquals(recipe,EditRecipe.parse(recipe.json()))
    }
    @Test fun undoAndRedoBranchAreDeterministic() {
        val history=EditHistory(EditRecipe())
        history.commit(EditRecipe(exposure=1f)); history.commit(EditRecipe(exposure=2f))
        assertEquals(1f,history.undo().exposure,0f)
        assertEquals(2f,history.redo().exposure,0f)
        history.undo(); history.commit(EditRecipe(filter="Mono"))
        assertFalse(history.canRedo); assertEquals(1f,history.undo().exposure,0f)
    }
    @Test(expected=IllegalArgumentException::class) fun invalidCropCannotBeSaved() { EditRecipe(left=.9f,right=.91f).validate() }
    @Test(expected=IllegalArgumentException::class) fun nonFiniteAdjustmentsAreRejected() { EditRecipe(exposure=Float.NaN).validate() }
    @Test(expected=IllegalArgumentException::class) fun invalidMarkupIsRejected() { EditRecipe(strokes=listOf(Stroke(listOf(MarkPoint(-1f,0f))))).validate() }
}
