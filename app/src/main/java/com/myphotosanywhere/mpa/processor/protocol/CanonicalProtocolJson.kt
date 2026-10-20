package com.myphotosanywhere.mpa.processor.protocol

import com.google.gson.JsonArray
import com.google.gson.JsonElement
import com.google.gson.JsonNull
import com.google.gson.JsonObject
import com.google.gson.JsonPrimitive
import com.google.gson.Strictness
import com.google.gson.stream.JsonReader
import com.google.gson.stream.JsonToken
import java.io.IOException
import java.io.StringReader
import java.nio.ByteBuffer
import java.nio.CharBuffer
import java.nio.charset.CharacterCodingException
import java.nio.charset.CodingErrorAction
import java.nio.charset.StandardCharsets

class ProcessorProtocolException(message: String, cause: Throwable? = null) : IllegalArgumentException(message, cause)

/** Portable integer-only RFC 8785 subset; has no Android/UI/storage dependency. */
class CanonicalProtocolJson(private val documentLimit: Int = MAX_DOCUMENT_BYTES) {
    init { require(documentLimit in 1..MAX_DOCUMENT_BYTES) }

    fun encode(input: ByteArray): ByteArray {
        if (input.isEmpty() || input.size > documentLimit) throw ProcessorProtocolException("Invalid protocol document byte length.")
        try {
            val text = StandardCharsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
                .onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(input)).toString()
            if (text.startsWith('\ufeff')) throw ProcessorProtocolException("JSON byte-order marks are not permitted.")
            val root = JsonReader(StringReader(text)).use { reader ->
                reader.strictness = Strictness.STRICT
                val value = read(reader, 0)
                if (reader.peek() != JsonToken.END_DOCUMENT) throw ProcessorProtocolException("Trailing JSON content.")
                value
            }
            val output = StringBuilder()
            write(root, output)
            return output.toString().toByteArray(StandardCharsets.UTF_8).also {
                if (it.size > documentLimit) throw ProcessorProtocolException("Canonical protocol document exceeds its byte bound.")
            }
        } catch (error: CharacterCodingException) {
            throw ProcessorProtocolException("Invalid Unicode scalar sequence.", error)
        } catch (error: IOException) {
            throw ProcessorProtocolException("Invalid JSON document.", error)
        }
    }

    private fun read(reader: JsonReader, depth: Int): JsonElement = when (reader.peek()) {
        JsonToken.BEGIN_OBJECT -> {
            if (depth >= MAX_DEPTH) throw ProcessorProtocolException("JSON nesting exceeds its bound.")
            reader.beginObject()
            val result = JsonObject()
            val names = HashSet<String>()
            while (reader.hasNext()) {
                if (names.size >= MAX_PROPERTIES) throw ProcessorProtocolException("Too many object properties.")
                val name = reader.nextName()
                checkString(name, MAX_NAME_BYTES)
                if (!names.add(name)) throw ProcessorProtocolException("Duplicate JSON property.")
                result.add(name, read(reader, depth + 1))
            }
            reader.endObject()
            result
        }
        JsonToken.BEGIN_ARRAY -> {
            if (depth >= MAX_DEPTH) throw ProcessorProtocolException("JSON nesting exceeds its bound.")
            reader.beginArray()
            val result = JsonArray()
            while (reader.hasNext()) {
                if (result.size() >= MAX_ITEMS) throw ProcessorProtocolException("Too many array entries.")
                result.add(read(reader, depth + 1))
            }
            reader.endArray()
            result
        }
        JsonToken.STRING -> JsonPrimitive(reader.nextString().also { checkString(it, MAX_STRING_BYTES) })
        JsonToken.NUMBER -> {
            val raw = reader.nextString()
            val number = raw.toLongOrNull()
            if (raw.contains('.') || raw.contains('e', ignoreCase = true) || number == null || number !in -MAX_INTEGER..MAX_INTEGER)
                throw ProcessorProtocolException("Protocol numbers must be safe integers.")
            JsonPrimitive(number)
        }
        JsonToken.BOOLEAN -> JsonPrimitive(reader.nextBoolean())
        JsonToken.NULL -> { reader.nextNull(); JsonNull.INSTANCE }
        else -> throw ProcessorProtocolException("Unexpected JSON token.")
    }

    private fun checkString(value: String, limit: Int) {
        if (value.length > limit) throw ProcessorProtocolException("Protocol string exceeds its bound.")
        val bytes = StandardCharsets.UTF_8.newEncoder().onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT).encode(CharBuffer.wrap(value))
        if (bytes.remaining() > limit) throw ProcessorProtocolException("Protocol string exceeds its byte bound.")
    }

    private fun write(value: JsonElement, output: StringBuilder) {
        when {
            value.isJsonObject -> {
                output.append('{')
                value.asJsonObject.entrySet().sortedBy { it.key }.forEachIndexed { index, entry ->
                    if (index != 0) output.append(',')
                    writeString(entry.key, output)
                    output.append(':')
                    write(entry.value, output)
                }
                output.append('}')
            }
            value.isJsonArray -> {
                output.append('[')
                value.asJsonArray.forEachIndexed { index, item ->
                    if (index != 0) output.append(',')
                    write(item, output)
                }
                output.append(']')
            }
            value.isJsonNull -> output.append("null")
            value.asJsonPrimitive.isString -> writeString(value.asString, output)
            value.asJsonPrimitive.isBoolean -> output.append(value.asBoolean)
            else -> output.append(value.asLong)
        }
    }

    private fun writeString(value: String, output: StringBuilder) {
        output.append('"')
        value.forEach { character ->
            when (character) {
                '"' -> output.append("\\\"")
                '\\' -> output.append("\\\\")
                '\b' -> output.append("\\b")
                '\u000c' -> output.append("\\f")
                '\n' -> output.append("\\n")
                '\r' -> output.append("\\r")
                '\t' -> output.append("\\t")
                else -> if (character.code < 32) output.append("\\u00").append(HEX[character.code shr 4]).append(HEX[character.code and 15])
                    else output.append(character)
            }
        }
        output.append('"')
    }

    companion object {
        const val MAX_DOCUMENT_BYTES = 16 * 1024 * 1024
        const val MAX_STRING_BYTES = 64 * 1024
        const val MAX_NAME_BYTES = 128
        const val MAX_ITEMS = 1000
        const val MAX_PROPERTIES = 256
        const val MAX_DEPTH = 32
        const val MAX_INTEGER = 9007199254740991L
        private const val HEX = "0123456789abcdef"
    }
}
