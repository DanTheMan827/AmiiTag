//
//  TagDump+FromID.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 3/3/21.
//  Copyright © 2021 Daniel Radtke. All rights reserved.
//

import Foundation

extension TagDump {
    static func FromID(id: Data, encrypt: Bool = true) throws -> TagDump {
        if id.count == 8 {
            var dumpData = Data()
            var salt = Data(count: 32)
            let _ = salt.withUnsafeMutableBytes {
                SecRandomCopyBytes(kSecRandomDefault, 32, $0.baseAddress!)
            }
            
            dumpData.append(Data([0x00, 0x00, 0x00, 0x88, 0x00, 0x00, 0x00, 0x00, 0x00, 0x48, 0x0f, 0xe0, 0xf1, 0x10, 0xff, 0xee, 0xa5, 0x00, 0x00, 0x00]))
            dumpData.append(Data(count: 64))
            dumpData.append(id)
            dumpData.append(Data(count: 4))
            dumpData.append(salt)
            dumpData.append(Data(count: 392))
            dumpData.append(Data([0x01, 0x00, 0x0F, 0xBD, 0x00, 0x00, 0x00, 0x04, 0x5F, 0x00, 0x00, 0x00]))
            
            if encrypt {
                let dump = TagDump(data: dumpData)!
                let patched = try dump.patchedDump(withUID: Data(dumpData[0..<9]), staticKey: KeyFiles.staticKey!, dataKey: KeyFiles.dataKey!, skipDecrypt: true)
                return patched
            } else {
                return TagDump(data: dumpData)!
            }
        } else {
            throw AmiiTagError(description: "ID is not 8 bytes")
        }
    }
}
