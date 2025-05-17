//
//  AmiiTagSwiftUIApp.swift
//  AmiiTagSwiftUI
//
//  Created by Daniel Radtke on 2/3/25.
//  Copyright © 2025 Daniel Radtke. All rights reserved.
//

import SwiftUI
import SwiftData

@main
struct AmiiTagSwiftUIApp: App {
    var sharedModelContainer: ModelContainer = {
        let schema = Schema([
            Item.self,
        ])
        let modelConfiguration = ModelConfiguration(schema: schema, isStoredInMemoryOnly: false)

        do {
            return try ModelContainer(for: schema, configurations: [modelConfiguration])
        } catch {
            fatalError("Could not create ModelContainer: \(error)")
        }
    }()

    var body: some Scene {
        WindowGroup {
            MainView()
        }
        .modelContainer(sharedModelContainer)
    }
}
