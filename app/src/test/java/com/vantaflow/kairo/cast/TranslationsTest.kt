package com.vantaflow.kairo.cast

import java.io.File
import javax.xml.parsers.DocumentBuilderFactory
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** Chaque clé de values/strings.xml (français, par défaut) existe en anglais, espagnol et arabe, avec les mêmes paramètres. */
class TranslationsTest {
    private val res = File("src/main/res")

    private fun strings(dir: String): Map<String, String> {
        val doc = DocumentBuilderFactory.newInstance().newDocumentBuilder().parse(File(res, "$dir/strings.xml"))
        val nodes = doc.getElementsByTagName("string")
        return (0 until nodes.length).associate { i ->
            val e = nodes.item(i) as org.w3c.dom.Element
            e.getAttribute("name") to e.textContent
        }
    }

    /** Paramètres de format triés (%1$s, %2$d…), %% exclu. */
    private fun formatArgs(s: String): List<String> =
        Regex("%(\\d+\\$)?[-#+ 0,(]*\\d*(\\.\\d+)?[a-zA-Z%]").findAll(s).map { it.value }.filter { it != "%%" }.sorted().toList()

    private val base by lazy { strings("values") }

    @Test fun defaultIsNotEmpty() = assertTrue(base.isNotEmpty())

    @Test fun english() = check("values-en")
    @Test fun spanish() = check("values-es")
    @Test fun arabic() = check("values-ar")

    private fun check(dir: String) {
        val t = strings(dir)
        assertEquals("$dir : clés manquantes", emptySet<String>(), base.keys - t.keys)
        assertEquals("$dir : clés en trop", emptySet<String>(), t.keys - base.keys)
        for ((k, v) in base) {
            val tr = t.getValue(k)
            assertTrue("$dir/$k vide", tr.isNotBlank())
            assertEquals("$dir/$k : paramètres de format", formatArgs(v), formatArgs(tr))
            assertTrue("$dir/$k : apostrophe non échappée", !Regex("(?<!\\\\)'").containsMatchIn(tr))
        }
    }
}
