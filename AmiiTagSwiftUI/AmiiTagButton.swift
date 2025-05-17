//
//  AmiiTagButton.swift
//  AmiiTagSwiftUI
//
//  Created by Daniel Radtke on 2/3/25.
//  Copyright © 2025 Daniel Radtke. All rights reserved.
//

import SwiftUI

struct AmiiTagButton: View {
    let title: String
    let action: () -> Void

    var body: some View {
        Button(action: {
            action()
        }) {
            Text(title)
                .padding()
                .foregroundColor(Color("ButtonTextColor")) // Text color from assets
                .background(Color("ButtonColor")) // Background color from assets
                .cornerRadius(0) // Optional: Rounded corners
                .scaledToFill()
                .frame(maxWidth: .infinity)
        }
        .scaledToFill()
        .frame(maxWidth: .infinity)
    }
}

#Preview {
    AmiiTagButton(title: "Custom Button") {
        
    }
}
