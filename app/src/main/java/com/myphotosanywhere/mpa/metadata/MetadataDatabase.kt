package com.myphotosanywhere.mpa.metadata

import androidx.room.*
import kotlinx.coroutines.flow.Flow

/** Generations make the offline outbox survive process death and edits during uploads. */
@Entity(tableName="metadata_states")
data class MetadataState(@PrimaryKey val bindingKey: String, val generation: Long=0,
    val uploadedGeneration: Long=0, val remoteId: String?=null, val etag: String?=null, val error: String?=null)
@Dao
interface MetadataDao {
    @Query("SELECT * FROM metadata_states") fun observe(): Flow<List<MetadataState>>
    @Query("SELECT * FROM metadata_states WHERE bindingKey=:key") suspend fun state(key: String): MetadataState?
    @Insert(onConflict=OnConflictStrategy.IGNORE) suspend fun insert(state: MetadataState)
    @Query("UPDATE metadata_states SET generation=generation+1 WHERE bindingKey=:key") suspend fun dirty(key: String)
    @Query("UPDATE metadata_states SET remoteId=:id, etag=:etag, uploadedGeneration=:generation, error=NULL WHERE bindingKey=:key")
    suspend fun uploaded(key: String,id: String,etag: String,generation: Long)
    @Query("UPDATE metadata_states SET remoteId=:id WHERE bindingKey=:key") suspend fun reserve(key: String,id: String)
    @Query("UPDATE metadata_states SET error=:error WHERE bindingKey=:key") suspend fun error(key: String,error: String?)
}
