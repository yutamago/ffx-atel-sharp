// SPDX-License-Identifier: MIT

package io.github.yutamago.atel.rider

import com.jetbrains.rider.settings.simple.SimpleOptionsPage

/** Settings | Tools | Atel# message text: shows the backend's page of the same id (AtelTextOptionsPage.cs). */
class AtelTextOptionsPage : SimpleOptionsPage("Atel# message text", PAGE_ID) {
    override fun getId(): String = PAGE_ID

    private companion object {
        const val PAGE_ID = "AtelTextOptionsPage"
    }
}
