package com.myphotosanywhere.mpa.selection

import org.junit.Assert.*
import org.junit.Test
import com.myphotosanywhere.mpa.drive.CloudPhoto

class MediaSelectionTest {
    private fun local(id: String)=SelectionTarget("photo:$id",id,id,null,20,"sha",null,null)
    private fun remote(version: String="1")=SelectionTarget("cloud:a:b", "remote",null,"a:b",20,null,"md5",version)
    @Test fun togglingUsesIdentityWithoutDependingOnGridOrder() {
        val selected=MediaSelection();selected.toggle(local("older"));selected.toggle(local("newer"))
        assertEquals(listOf("photo:older","photo:newer"),selected.snapshot().map { it.key })
        selected.toggle(local("older"));assertEquals(listOf(local("newer")),selected.snapshot())
    }
    @Test fun verifiedRepresentationChangeDoesNotDuplicateSelection() {
        val selected=MediaSelection(listOf(remote()))
        val linked=local("downloaded").copy(cloudKey="a:b",md5="md5",version="1")
        assertTrue(selected.contains(linked));selected.toggle(linked);assertTrue(selected.snapshot().isEmpty())
    }
    @Test fun sourcesWithSameRemoteIdRemainDistinct() {
        val selected=MediaSelection(listOf(remote()))
        selected.toggle(remote().copy(key="cloud:other:b",cloudKey="other:b"));assertEquals(2,selected.snapshot().size)
    }
    @Test fun differentLocalIdentitiesDoNotMatchThroughAReboundCloudKey() {
        assertFalse(local("one").copy(cloudKey="a:b").matches(local("two").copy(cloudKey="a:b")))
        assertFalse(remote().matches(local("changed").copy(cloudKey="a:b",md5="different",version="2")))
    }
    @Test fun retainedSelectionIsBoundedAndLimitIsExplicit() {
        val selected=MediaSelection((0 until MediaSelection.MAX_ITEMS).map { local("$it") })
        assertFalse(selected.toggle(local("overflow")));assertEquals(MediaSelection.MAX_ITEMS,selected.snapshot().size)
        assertTrue(selected.toggle(local("0")));assertTrue(selected.toggle(local("overflow")))
    }
    @Test fun unknownCloudVersionNeverMatchesAChangedKnownVersion() {
        val cloud=CloudPhoto("a:b","a","b","folder","remote","image/jpeg",20,null,"2",null,null,null,null)
        assertFalse(remote().copy(md5=null).accepts(cloud))
        assertTrue(remote("2").copy(md5=null).accepts(cloud))
        assertFalse(remote("2").copy(md5=null).accepts(cloud.copy(removed=true)))
        assertFalse(remote("2").copy(md5=null).accepts(cloud.copy(bytes=21)))
    }
    @Test fun checksumsCanVerifyUnchangedContentAcrossVersionChanges() {
        val cloud=CloudPhoto("a:b","a","b","folder","remote","image/jpeg",20,"md5","2",null,null,null,null)
        assertTrue(remote().accepts(cloud));assertFalse(remote().accepts(cloud.copy(md5="changed")))
    }
    @Test(expected=IllegalArgumentException::class) fun nonCanonicalIdentityFailsFast() { local("one").copy(key="photo:two") }
    @Test(expected=IllegalArgumentException::class) fun duplicateInitialIdentityFailsFast() { MediaSelection(listOf(local("one"),local("one"))) }
    @Test(expected=IllegalArgumentException::class) fun albumOperationWithoutDestinationFailsFast() { BulkAction(BulkKind.ALBUM) }
    @Test(expected=IllegalArgumentException::class) fun unrelatedOperationWithAlbumFailsFast() { BulkAction(BulkKind.FAVORITE,"album") }
}
