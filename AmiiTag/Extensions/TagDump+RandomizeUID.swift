//
//  TagDump+RandomizeUID.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 6/20/23.
//  Copyright © 2023 Daniel Radtke. All rights reserved.
//

import Foundation

extension TagDump {
    func randomizeUID() throws -> TagDump {
        if KeyFiles.hasKeys {
            let randomUidSig: (key: String, value: Data) = {
                if NTAG215Tag.uidSignatures.count > 0,
                   let element = NTAG215Tag.uidSignatures.randomElement() {
                    return element
                }
                
                // Return a fake uid/sig pair
                return (key: NTAG215Tag.getRandomUID().ToHexString(), value: Data(count: 32))
            }()
            
            if let patched = try? self.patchedDump(withUID: Data(hex: randomUidSig.key), staticKey: KeyFiles.staticKey!, dataKey: KeyFiles.dataKey!) {
                return TagDump(data: Data(patched.data[0..<532] + Data(count: 8) + randomUidSig.value))!
            }
            
            throw AmiiTagError(description: "Error patching dump.")
        }
        
        throw AmiiTagError(description: "Key files missing.")
    }
}
