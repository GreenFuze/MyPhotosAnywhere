package com.myphotosanywhere.mpa.core

enum class AiProvider(val title: String, val guidance: String) {
    CHATGPT("ChatGPT", "Choose the official ChatGPT app in the share sheet. Image editing depends on your plan and available tools."),
    GEMINI("Gemini", "Choose the official Gemini app in the share sheet. Image editing depends on your plan and available tools."),
    CLAUDE("Claude", "Choose the official Claude app for analysis or editing instructions. Its image input does not guarantee image generation."),
}

object EditPrompt {
    fun build(instructions: String): String {
        require(instructions.isNotBlank()) { "Describe the edit first." }
        require(instructions.length <= 4000) { "Keep instructions below 4,000 characters." }
        return "Edit the attached photo using these instructions:\n\n${instructions.trim()}\n\n" +
            "Preserve everything I have not asked to change. If your tools support it, return the edited image so I can save it. " +
            "Otherwise explain the steps or tools needed."
    }
}
