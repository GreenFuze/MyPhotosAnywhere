package com.myphotosanywhere.mpa.data

import androidx.room.*
import kotlinx.coroutines.flow.Flow

@Entity(tableName = "albums")
data class Album(@PrimaryKey val id: String, val name: String, val createdAt: Long, val pinned: Boolean = false)
@Entity(tableName = "album_photos", primaryKeys = ["albumId", "photoId"], indices = [Index("photoId")])
data class AlbumPhoto(val albumId: String, val photoId: String)
@Entity(tableName = "edit_drafts")
data class EditDraft(@PrimaryKey val photoId: String, val recipe: String, val updatedAt: Long)
@Entity(tableName = "people")
data class Person(@PrimaryKey val id: String, val name: String)
@Entity(tableName = "faces", indices = [Index("photoId"), Index("personId")])
data class FaceRecord(@PrimaryKey val id: String, val photoId: String, val cropPath: String,
    val embedding: ByteArray?, val personId: String? = null, val suggestedPersonId: String? = null,
    val similarity: Float? = null, val corrected: Boolean = false,
    val left: Float? = null, val top: Float? = null, val right: Float? = null, val bottom: Float? = null,
    val imageWidth: Int? = null, val imageHeight: Int? = null, val modelVersion: String? = null)
@Entity(tableName = "face_scans")
data class FaceScan(@PrimaryKey val photoId: String, val sha256: String, val error: String? = null,
    @ColumnInfo(defaultValue = "''") val modelVersion: String = "")

@Dao
interface LibraryDao {
    @Query("SELECT * FROM albums ORDER BY name COLLATE NOCASE") fun albums(): Flow<List<Album>>
    @Query("SELECT * FROM albums") suspend fun allAlbums(): List<Album>
    @Query("SELECT * FROM album_photos") suspend fun allMemberships(): List<AlbumPhoto>
    @Query("SELECT * FROM album_photos") fun memberships(): Flow<List<AlbumPhoto>>
    @Query("SELECT * FROM album_photos WHERE albumId = :id") suspend fun members(id: String): List<AlbumPhoto>
    @Insert(onConflict = OnConflictStrategy.REPLACE) suspend fun saveAlbum(album: Album)
    @Insert(onConflict = OnConflictStrategy.IGNORE) suspend fun addToAlbum(member: AlbumPhoto)
    @Query("DELETE FROM album_photos WHERE albumId = :album AND photoId = :photo") suspend fun removeFromAlbum(album: String, photo: String)
    @Query("DELETE FROM album_photos WHERE albumId = :id") suspend fun clearAlbum(id: String)
    @Query("DELETE FROM albums WHERE id = :id") suspend fun deleteAlbum(id: String)
    @Query("SELECT COUNT(*) FROM album_photos JOIN albums ON albums.id = album_photos.albumId WHERE photoId = :id AND albums.pinned = 1")
    suspend fun pinnedAlbumCount(id: String): Int
    @Query("SELECT * FROM edit_drafts WHERE photoId = :id") suspend fun draft(id: String): EditDraft?
    @Insert(onConflict = OnConflictStrategy.REPLACE) suspend fun saveDraft(draft: EditDraft)
    @Query("DELETE FROM edit_drafts WHERE photoId = :id") suspend fun deleteDraft(id: String)
    @Query("SELECT * FROM people ORDER BY name COLLATE NOCASE") fun people(): Flow<List<Person>>
    @Query("SELECT * FROM people") suspend fun allPeople(): List<Person>
    @Insert(onConflict = OnConflictStrategy.REPLACE) suspend fun savePerson(person: Person)
    @Query("DELETE FROM people WHERE id = :id") suspend fun deletePerson(id: String)
    @Query("SELECT * FROM faces ORDER BY photoId, id") fun faces(): Flow<List<FaceRecord>>
    @Query("SELECT * FROM faces") suspend fun allFaces(): List<FaceRecord>
    @Query("SELECT * FROM faces WHERE id = :id") suspend fun face(id: String): FaceRecord?
    @Insert(onConflict = OnConflictStrategy.REPLACE) suspend fun saveFace(face: FaceRecord)
    @Query("UPDATE faces SET personId = :target, suggestedPersonId = NULL, similarity = NULL, corrected = 1 WHERE personId = :source")
    suspend fun mergePeople(source: String, target: String)
    @Query("UPDATE faces SET suggestedPersonId = NULL, similarity = NULL WHERE suggestedPersonId = :id") suspend fun clearSuggestions(id: String)
    @Query("UPDATE faces SET personId = NULL, corrected = 1 WHERE personId = :id") suspend fun unassignPerson(id: String)
    @Query("SELECT * FROM face_scans WHERE photoId = :id") suspend fun scan(id: String): FaceScan?
    @Insert(onConflict = OnConflictStrategy.REPLACE) suspend fun saveScan(scan: FaceScan)
    @Query("DELETE FROM face_scans WHERE error IS NOT NULL") suspend fun retryScans()
    @Query("DELETE FROM faces WHERE photoId=:id") suspend fun deletePhotoFaces(id: String)
    @Query("DELETE FROM faces") suspend fun deleteFaces()
    @Query("DELETE FROM face_scans") suspend fun deleteScans()
    @Query("DELETE FROM people") suspend fun deletePeople()
}
