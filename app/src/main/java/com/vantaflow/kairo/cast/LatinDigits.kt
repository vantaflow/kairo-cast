package com.vantaflow.kairo.cast

import android.content.Context
import java.util.Locale

/**
 * Texte traduit dont les nombres restent en chiffres 0-9, quelle que soit la langue
 * (en arabe, `getString(id, n)` les écrirait en chiffres arabes-indiens).
 */
fun Context.latinString(id: Int, vararg args: Any): String =
    String.format(Locale.ROOT, getString(id), *args)
