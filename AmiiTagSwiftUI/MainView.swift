//
//  ContentView.swift
//  AmiiTagSwiftUI
//
//  Created by Daniel Radtke on 2/3/25.
//  Copyright © 2025 Daniel Radtke. All rights reserved.
//

import SwiftUI
import SwiftData

struct MainView: View {
    @Environment(\.modelContext) private var modelContext
    @Query private var items: [Item]
    
    func doNothing() {
        
    }

    var body: some View {
        ScrollView {
            LazyVStack {
                Image(ImageResource.amiitag)
                    .resizable()
                    .scaledToFit()
                    .safeAreaPadding()
                    .frame(maxWidth: 400)
                AmiiTagButton(title: "Test") {
                    
                }
                .scaledToFit()
            }
        }
    }
}

#Preview {
    MainView()
        .modelContainer(for: Item.self, inMemory: true)
}
