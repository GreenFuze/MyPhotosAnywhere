package com.myphotosanywhere.mpa.trash

/** A remaining live representation preserves visibility; Archive and offline are not Trash. */
internal object TrashVisibility {
    const val PHOTO_SQL="""NOT EXISTS(SELECT 1 FROM drive_trash t WHERE t.photoId=p.id AND t.confirmed=1
        AND NOT EXISTS(SELECT 1 FROM cloud_photos active JOIN drive_sources src ON src.key=active.bindingKey
        WHERE active.photoId=p.id AND active.removed=0 AND src.enabled=1 AND NOT EXISTS(
          SELECT 1 FROM drive_trash x WHERE x.accountId=src.accountId AND x.remoteId=active.remoteId AND x.confirmed=1)))"""
}
